"""Independent file-only watch-edit and interruption oracle. Never invokes TIA.

Uses only the existing evidence reader/XML key utilities, not production editing,
semantic hashes or parsers. Native IDs and all unknown payload remain significant.
"""
import argparse
import copy
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from xml.parsers import expat

import verify_watch_workflow as base

need, load, sha, ref, artifact = base.need, base.load, base.sha, base.ref, base.artifact
VerificationError = base.VerificationError
PREFIX = base.PREFIX
ROW_KINDS = {PREFIX + "PlcWatchTableEntry", PREFIX + "PlcTableCommentEntry"}
NATIVE_POINTS = {"checkpoint.beforeArchive", "checkpoint.afterArchive", "import.before", "import.after",
                 "save.before", "save.after", "retrieve.before", "retrieve.after"}
HOST_POINTS = {"host.watch_checkpoint", "host.watch_apply_native", "host.watch_restore_native"}


external_xml_nodes = base.external_xml_nodes

def native_key(data): return base.xml_key(document(data)), external_xml_nodes(data)


def document(data):
    root = base.xml(data); table = base.table(root)
    need(len(root.findall("DocumentInfo")) == 1 and root.findtext("DocumentInfo/ExportSetting") == "WithDefaults", "Exact retained WithDefaults profile required.")
    need(len(table.findall("AttributeList")) == 1 and len(table.findall("AttributeList/Name")) == 1 and len(table.findall("ObjectList")) <= 1, "Ambiguous table metadata.")
    ids = []
    for item in root.iter():
        if not isinstance(item.tag, str): continue
        if "ID" in item.attrib:
            text = item.attrib["ID"]
            need(re.fullmatch(r"[0-9A-Fa-f]{1,8}", text), "Invalid native ID."); ids.append(int(text, 16))
        if item is table or item.tag.startswith(PREFIX) or item.tag in ("MultilingualText", "MultilingualTextItem"):
            need("ID" in item.attrib, "Native object ID is missing.")
        need(len(item.findall("AttributeList")) <= 1 and len(item.findall("ObjectList")) <= 1, "Ambiguous native object containers.")
        if item.tag == "AttributeList": need(len({c.tag for c in item if isinstance(c.tag, str)}) == len([c for c in item if isinstance(c.tag, str)]), "Duplicate native attribute.")
    need(len(set(ids)) == len(ids), "Duplicate numeric native IDs.")
    rows = row_elements(root)
    need(len(rows) <= 10000 and all(r.tag in ROW_KINDS and r.attrib.get("CompositionName") == "Entries" for r in rows), "Unsupported row profile.")
    for row in rows:
        cultures = [c.text for c in row.findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem/AttributeList/Culture")]
        need(len(cultures) == len(set(cultures)), "Duplicate comment culture.")
    return root


def row_elements(root):
    objects = base.table(root).find("ObjectList")
    return [] if objects is None else [c for c in objects if isinstance(c.tag, str)]


def ordinal(value, length, allow_end=False):
    need(type(value) is int and 0 <= value < length + int(allow_end), "Exact bounded row ordinal required.")
    return value


def remove_element_preserving_tail(parent, child):
    children = list(parent); index = children.index(child)
    if child.tail:
        if index: children[index - 1].tail = (children[index - 1].tail or "") + child.tail
        else: parent.text = (parent.text or "") + child.tail
    parent.remove(child)


def exact_delta(before, candidate, edit):
    old, actual = document(before), document(candidate)
    need(external_xml_nodes(before) == external_xml_nodes(candidate), "Candidate changes unknown prolog/epilog comments or processing instructions.")
    expected = copy.deepcopy(old); rows = row_elements(expected)
    need(isinstance(edit, dict), "Explicit reviewed edit required.")
    kind = edit.get("kind", "modifyValue" if edit.get("field") == "ModifyValue" else None)
    fields = {"modifyValue": {"ordinal", "field", "before", "after"}, "comment": {"kind", "ordinal", "culture", "text"},
              "clone": {"kind", "ordinal", "insertAt"}, "reorder": {"kind", "order"}, "delete": {"kind", "ordinal"}}
    need(kind in fields and set(edit) == fields[kind], "Unsupported or ambiguous reviewed edit fields.")
    if kind in ("modifyValue", "comment", "clone", "delete"):
        selected = rows[ordinal(edit["ordinal"], len(rows))]
    if kind == "modifyValue":
        value = selected.find("AttributeList/ModifyValue")
        need(selected.findtext("AttributeList/DisplayFormat") == "Bool" and value is not None and len(value) == 0 and
             value.text == edit["before"] and edit["after"] in ("TRUE", "FALSE"), "Exact existing Bool change required.")
        value.text = edit["after"]
    elif kind == "comment":
        need(isinstance(edit["culture"], str) and isinstance(edit["text"], str), "Exact culture and text required.")
        values = [a.find("Text") for a in selected.findall("ObjectList/MultilingualText[@CompositionName='Comment']/ObjectList/MultilingualTextItem/AttributeList")
                  if a.findtext("Culture") == edit["culture"]]
        need(len(values) == 1 and values[0] is not None and len(values[0]) == 0, "One existing plain-text culture item required.")
        values[0].text = edit["text"]
    elif kind == "delete":
        remove_element_preserving_tail(base.table(expected).find("ObjectList"), selected)
    elif kind == "reorder":
        order = edit["order"]
        need(isinstance(order, list) and all(type(i) is int for i in order) and sorted(order) == list(range(len(rows))), "Reorder requires an exact row permutation.")
        objects = base.table(expected).find("ObjectList")
        for previous, replacement in zip(rows, [copy.deepcopy(rows[i]) for i in order]):
            index = list(objects).index(previous); replacement.tail = previous.tail
            objects.remove(previous); objects.insert(index, replacement)
    else:
        at = ordinal(edit["insertAt"], len(rows), allow_end=True)
        new_rows = row_elements(actual)
        need(len(new_rows) == len(rows) + 1, "Clone must add exactly one row.")
        clone = copy.deepcopy(new_rows[at]); original_clone = copy.deepcopy(selected)
        old_nodes, new_nodes = list(original_clone.iter()), list(clone.iter())
        need(len(old_nodes) == len(new_nodes), "Clone payload shape changed.")
        existing_ids = {int(n.attrib["ID"], 16) for n in old.iter() if "ID" in n.attrib}
        for source, added in zip(old_nodes, new_nodes):
            need(source.tag == added.tag, "Clone payload node order changed.")
            if isinstance(source.tag, str):
                need(not any("ref" in key.lower() or key.lower().endswith("id") and key != "ID" for key in source.attrib) and
                     not ("ref" in source.tag.lower() or source.tag.lower().endswith("id")), "Referenced row payload needs a separately qualified clone profile.")
            if "ID" in source.attrib:
                need("ID" in added.attrib and int(added.attrib["ID"], 16) not in existing_ids, "Clone must allocate fresh IDs without reusing any baseline ID.")
                source.attrib["ID"] = added.attrib["ID"]
        original_clone.tail = clone.tail = None
        need(base.xml_key(original_clone, False) == base.xml_key(clone, False), "Clone changed full payload beyond fresh IDs.")
        objects = base.table(expected).find("ObjectList")
        if at == len(rows): objects.append(clone)
        else: objects.insert(list(objects).index(rows[at]), clone)
    need(base.xml_key(old, False) != base.xml_key(actual, False), "Reviewed edit makes no change.")
    need(base.xml_key(expected, False) == base.xml_key(actual, False), "Candidate changes IDs, order, cultures or unknown payload beyond the exact reviewed edit.")
    return {"kind": kind, "beforeRows": len(row_elements(old)), "candidateRows": len(row_elements(actual)),
            "nativeIdsPreservedExceptFreshCloneIds": True, "unknownPayloadPreserved": True}


def typed_rows(snapshot, tree):
    target = snapshot["target"]; watch = base.table(tree)
    need(target["kind"] == "watch" and target["name"] == watch.findtext("AttributeList/Name") and target["isConsistent"] is True,
         "Typed native table identity/consistency differs.")
    rows = row_elements(tree); need(len(rows) == len(target["rows"]), "Typed native row count differs.")
    for row, observed in zip(rows, target["rows"]):
        need(observed["kind"] == row.tag[len(PREFIX):], "Typed row kind/order differs.")
        if row.tag == PREFIX + "PlcWatchTableEntry":
            for field in ("Name", "Address", "DisplayFormat", "ModifyValue"):
                need((observed.get(field[0].lower() + field[1:]) or "") == (row.findtext("AttributeList/" + field) or ""), "Typed native row field differs: " + field)
            need(observed["modifyIntention"] is False and row.findtext("AttributeList/ModifyIntention", "false").lower() == "false", "Offline edit armed a modify action.")


def snapshot_check(root, snapshot, expected, before=None, fresh=False, project_path=None):
    path = artifact(root, snapshot["target"]["artifact"]); tree = document(path.read_bytes())
    typed_rows(snapshot, tree)
    need((base.xml_key(tree), external_xml_nodes(path.read_bytes())) == native_key(expected), "Complete native XML differs from reviewed document, including native IDs.")
    observation = snapshot["observation"]
    need(observation["offline"] is True and observation["reference"]["contentSha256"] == snapshot["target"]["semanticSha256"], "Offline/native content observation differs.")
    need(observation["projectFilePath"] == snapshot["project"]["path"], "Native project identity is inconsistent.")
    if project_path is not None: need(snapshot["project"]["path"] == project_path, "Snapshot belongs to a different project.")
    for entry in snapshot["tables"]: artifact(root, entry["artifact"])
    targets = [t for t in snapshot["tables"] if all(t.get(k) == snapshot["target"].get(k) for k in ("kind", "group", "name"))]
    need(targets == [snapshot["target"]], "Complete table inventory does not contain exactly the same target evidence.")
    retained = load(artifact(root, snapshot["receipt"]))
    need(retained == {k: v for k, v in snapshot.items() if k not in ("receipt", "saved", "automaticReplay")}, "Retained snapshot differs from returned evidence.")
    if before is not None:
        old = before["observation"]
        for key in ("adapterSha256", "profileSha256", "hardwareIdentity", "nativeIdentity"):
            need(observation.get(key) == old.get(key), "Native observation changed bound " + key)
        need({p["name"] for p in observation["probes"] if p["state"] == "observed"} >= {"offline", "watchImport"}, "Required native capability probes missing.")
        base.peer_check(root, before, snapshot)
        need(peer_bytes(root, before) == peer_bytes(root, snapshot), "Peer bytes changed beyond the retained Created timestamp.")
    if fresh:
        need(before is not None and snapshot["processId"] != before["processId"] and observation["sessionEpoch"] != before["observation"]["sessionEpoch"] and
             snapshot["projectModified"] is False, "Fresh saved native process/session required.")
    return path


def peer_bytes(root, snapshot):
    values = {}
    for entry in snapshot["tables"]:
        if all(entry.get(k) == snapshot["target"].get(k) for k in ("kind", "group", "name")): continue
        key = entry["kind"], entry["group"], entry["name"]
        need(key not in values, "Duplicate peer identity."); values[key] = base.without_created_bytes(artifact(root, entry["artifact"]).read_bytes())
    for path in artifact(root, snapshot["target"]["artifact"]).parent.glob("*.xml"):
        if path.name.startswith(("block-", "type-", "tags-")):
            tree = base.xml(path.read_bytes())
            objects = [e for e in tree if isinstance(e.tag, str) and e.tag not in ("Engineering", "DocumentInfo")]
            need(len(objects) == 1, "Ambiguous peer XML identity.")
            key = path.name.split("-")[0], objects[0].tag, objects[0].findtext("AttributeList/Name")
            need(key not in values, "Duplicate peer identity."); values[key] = base.without_created_bytes(path.read_bytes())
    return values


def frozen_sources(root, admission, before):
    worker = Path(admission["worker"])
    need(sha(worker.read_bytes()) == admission["workerSha256"] == before["observation"]["adapterSha256"], "Admitted worker/baseline digest differs.")
    need(admission["nativeSequential"] is True, "Sequential native admission is missing.")
    candidates = [p / name for p in worker.parents for name in ("manifest.json", "source-manifest.json") if (p / name).is_file()]
    need(candidates, "Frozen source manifest missing.")
    manifest = candidates[0]; entries = load(manifest)
    need(isinstance(entries, list) and entries, "Frozen source manifest is empty or unsupported.")
    for entry in entries:
        path = Path(entry["path"])
        if not path.is_absolute(): path = manifest.parent / path
        need(path.is_file() and sha(path.read_bytes()) == entry["sha256"], "Frozen source changed: " + str(path))
    for name in ("WatchTableDocument.cs", "WatchTableDispatcher.cs", "OpennessSession.WatchTables.cs", "EngineeringOperationStore.cs"):
        need(any(Path(entry["path"]).name == name for entry in entries), "Frozen source missing: " + name)
    return {"manifest": ref(manifest), "worker": ref(worker), "verifiedFiles": len(entries)}


def reviewed_candidate(root, check_sources=True):
    root = Path(root).resolve(); outer = load(root / "review.json"); record, review = outer["operation"], outer["review"]
    need(record["state"] == "prepared" and record["revision"] == 0 and record["previousSha256"] is None, "Exact initial prepared operation required.")
    need(base.canonical_sha(record["plan"]) == record["planSha256"], "Reviewed plan digest mismatch.")
    need(record["plan"]["target"] == review["before"]["observation"], "Prepared target differs from reviewed native observation.")
    need(len(record["plan"]["inputs"]) == 2, "Review and candidate inputs must be exact.")
    need(outer["reviewArtifact"] == record["plan"]["inputs"][0] and review["input"] == record["plan"]["inputs"][1], "Input reference aliases conflict.")
    need(load(artifact(root, outer["reviewArtifact"])) == review, "Retained review differs from the prepared review.")
    candidate = artifact(root, review["input"]); before = review["before"]
    baseline = artifact(root, before["target"]["artifact"])
    snapshot_check(root, before, baseline.read_bytes())
    need(before["projectModified"] is False, "Reviewed native baseline is unsaved.")
    need(review["selector"]["tableName"] == before["target"]["name"] == "MilestoneWatch", "Exact owned fixture table required.")
    delta = exact_delta(baseline.read_bytes(), candidate.read_bytes(), review["edit"])
    need(record["plan"]["operation"] == ("watchLiteralEdit" if delta["kind"] == "modifyValue" else "watchDocumentEdit"), "Plan operation does not match reviewed edit.")
    source = frozen_sources(root, load(root / "admission.json"), before) if check_sources else None
    return {"operationId": record["id"], "reviewedPlanSha256": record["planSha256"], "reviewedInputSha256": sha(candidate.read_bytes()),
            "baseline": ref(baseline), "candidate": ref(candidate), "delta": delta, "frozenSources": source, "nativeInvoked": False, "noOnlineAction": True}


def journal(root, operation_id):
    paths = sorted((root / "engineering-operations" / operation_id).glob("[0-9]*.json"))
    need(paths, "Durable operation records missing."); records = [load(p) for p in paths]
    for index, record in enumerate(records):
        need(paths[index].stem == f"{index:06}" and record["revision"] == index and record["id"] == operation_id, "Journal revision/identity gap.")
        need(record["planSha256"] == base.canonical_sha(record["plan"]) and record["plan"] == records[0]["plan"], "Immutable journal plan changed.")
        need(record["previousSha256"] == (base.canonical_sha(records[index - 1]) if index else None), "Journal hash chain changed.")
        need(len({e["gate"] for e in record["evidence"]}) == len(record["evidence"]), "Duplicate durable evidence gates.")
        for item in record["plan"]["inputs"] + record["checkpoints"] + [e["artifact"] for e in record["evidence"]] + [r["evidence"] for r in record["reconciliation"]] + [r["evidence"] for r in record.get("recoveryResolutions", [])]: artifact(root, item)
    transitions = [r["state"] for i, r in enumerate(records) if i == 0 or r["state"] != records[i - 1]["state"]]
    need(transitions.count("dispatchIntent") <= 1 and transitions.count("checkpointIntent") <= 1 and transitions.count("recoveryIntent") <= 1,
         "A native intent was replayed.")
    need(all(len(r["checkpoints"]) == 1 for r in records if r["state"] == "dispatchIntent"), "Native mutation dispatch requires exactly one retained checkpoint.")
    return records


def original_disposition(root, snapshot, baseline, candidate):
    actual = native_key(artifact(root, snapshot["target"]["artifact"]).read_bytes())
    if actual == native_key(baseline): return "unchanged", baseline
    if actual == native_key(candidate): return "verifiedApplied", candidate
    raise VerificationError("Original project contains an unreviewed outcome.")


def destination_manifest(root, manifest, disposition, destination):
    need(manifest["destination"] == destination and manifest["restoredProjectVerified"] is False and manifest["scope"] == "retainedRegularFileBytesOnly", "Destination inspection scope or identity differs.")
    folder = Path(destination); need(folder.resolve().is_relative_to(root.resolve()), "Recovery destination escapes run root.")
    need(folder.exists() == manifest["exists"], "Recovery destination existence changed.")
    directories, files = [], []
    if folder.exists():
        need(folder.is_dir() and not folder.is_symlink() and not folder.is_junction(), "Unsafe recovery directory.")
        for path in folder.rglob("*"):
            need(not path.is_symlink() and not path.is_junction(), "Recovery inspection never follows links.")
            relative = path.relative_to(folder).as_posix()
            if path.is_dir(): directories.append(relative)
            else:
                need(path.is_file(), "Unexpected recovery device entry."); files.append({"relativePath": relative, "bytes": path.stat().st_size, "sha256": sha(path.read_bytes())})
    need(sorted(directories) == sorted(d.replace('\\', '/') for d in manifest["directories"]) and
         sorted(files, key=lambda f: f["relativePath"]) == sorted([dict(f, relativePath=f["relativePath"].replace('\\', '/')) for f in manifest["files"]], key=lambda f: f["relativePath"]), "Retained recovery-directory manifest changed.")
    need(sum(f["bytes"] for f in files) == manifest["totalBytes"], "Recovery manifest byte total differs.")
    need((not files and not directories) if disposition == "verifiedAbsent" else (disposition == "retainedPartial" and bool(files or directories)), "Recovery disposition disagrees with exact retained output.")


def recovery_check(root, records, review, operation, baseline, candidate, interrupted):
    final = records[-1]; before = review["before"]
    need(final["state"] == "reconciled" and load(root / "reconciled.json")["operation"] == final, "Final reconciliation is missing or differs from durable journal.")
    need(len(final["reconciliation"]) == 1, "One independently reconciled original resource required.")
    resolution = final["reconciliation"][0]
    need(resolution["resourceKey"] == final["plan"]["resources"][0]["resourceKey"], "Original reconciliation resource changed.")
    proofs = {e["gate"]: load(artifact(root, e["artifact"])) for e in final["evidence"]}
    restored_proof = proofs.get("recovery.retrieve", proofs.get("recovery.located"))
    if restored_proof is None:
        need(interrupted, "Normal workflow has no verified restoration.")
        proof = load(artifact(root, resolution["evidence"])); original = proof["original"]
        need(proof["automaticReplay"] is False and proof["nativeRestoreInvoked"] is False and proof["restoredProjectVerified"] is False, "Interrupted inspection cannot claim restoration or replay.")
        disposition, expected = original_disposition(root, original, baseline, candidate)
        need(disposition == proof["originalDisposition"] == resolution["disposition"], "Original disposition is not independently supported.")
        if not any(r["state"] == "dispatchIntent" for r in records): need(disposition == "unchanged", "An undispatched edit cannot explain an applied original.")
        snapshot_check(root, original, expected, before, True, operation["projectPath"])
        destinations, resolutions = final["recoveryDestinations"], final["recoveryResolutions"]
        need(len(destinations) == len(resolutions) <= 1, "Every admitted destination needs an independent resolution.")
        if destinations:
            dest = destinations[0]; resolved = resolutions[0]
            need(resolved["resourceKey"] == dest["resourceKey"] == resolution["resourceKey"] and resolved["recoveryLocation"] == dest["recoveryLocation"], "Recovery destination identity differs.")
            need(resolved["disposition"] == proof["destinationDisposition"] and resolved["evidence"] == resolution["evidence"], "Destination resolution proof differs.")
            destination_manifest(root, proof["destination"], resolved["disposition"], dest["recoveryLocation"])
        else: need(proof["destination"] is None and proof["destinationDisposition"] is None, "Unadmitted destination evidence appeared.")
        return {"originalDisposition": disposition, "restoredProjectVerified": False, "destinationDisposition": proof["destinationDisposition"], "finalState": final["state"]}
    need(len(final["checkpoints"]) == len(final["recoveryDestinations"]) == len(final["recoveryResolutions"]) == 1, "Restoration needs one exact archive and destination.")
    artifact(root, final["checkpoints"][0])
    located = "recovery.located" in proofs
    if located:
        need(restored_proof["retrievalResponseRetained"] is False and restored_proof["nativeRestoreInvoked"] is False and restored_proof["automaticReplay"] is False,
             "Located recovery must not claim a retained return or repeated retrieval.")
        packet = load(root / "located.json"); need(packet["located"] is True and load(artifact(root, packet["evidence"])) == restored_proof, "Located retrieval response evidence differs.")
    else: need(restored_proof["checkpoint"] == final["checkpoints"][0], "Retrieved archive differs from the bound checkpoint.")
    restored, original = proofs["recovery.restored"], proofs["recovery.original"]
    recovered_path = restored_proof["recovered"]["project"]["path"]
    destination = final["recoveryDestinations"][0]["recoveryLocation"]
    need(Path(recovered_path).resolve().is_relative_to(Path(destination).resolve()) and recovered_path != operation["projectPath"], "Restored project escapes the exact admitted destination.")
    for snap in (restored_proof["restored"], restored): snapshot_check(root, snap, baseline, before, True, recovered_path)
    disposition, expected = original_disposition(root, original, baseline, candidate)
    if not interrupted or (root / "verified.json").is_file(): need(disposition == "verifiedApplied", "Previously verified applied original changed during recovery.")
    snapshot_check(root, original, expected, before, True, operation["projectPath"])
    need(resolution["disposition"] == disposition and load(artifact(root, resolution["evidence"])) == original, "Original reconciliation disposition/proof differs.")
    recovered_resolution = final["recoveryResolutions"][0]
    need(recovered_resolution["disposition"] == "verifiedRestored" and recovered_resolution["recoveryLocation"] == destination and
         recovered_resolution["resourceKey"] == resolution["resourceKey"] and load(artifact(root, recovered_resolution["evidence"])) == restored, "Restored disposition/proof differs.")
    snapshots = [before, restored_proof["restored"], restored, original]
    if (root / "verified.json").is_file(): snapshots.append(load(root / "verified.json")["reopened"])
    need(len({s["processId"] for s in snapshots}) == len(snapshots) and len({s["observation"]["sessionEpoch"] for s in snapshots}) == len(snapshots), "Recovery and reopen phases reused a native process/session.")
    need(restored_proof["restored"]["processId"] == restored_proof["recovered"]["processId"] and
         restored_proof["restored"]["observation"]["sessionEpoch"] == restored_proof["recovered"]["sessionEpoch"], "Recovery response and snapshot native identity differ.")
    need(load(root / "restored-verified.json")["verified"] == "restored", "Separate fresh restored verification response missing.")
    return {"originalDisposition": disposition, "restoredProjectVerified": True, "retrievalResponseRetained": not located, "recoveryPath": recovered_path, "finalState": final["state"]}


def interruption_check(root, admission, records, operation):
    mode = admission["mode"]; marker = load(root / "interruption-marker.json")
    need(mode in NATIVE_POINTS | HOST_POINTS and marker["point"] == mode and marker["automaticReplay"] is False, "Unknown or unbound interruption marker.")
    need(any(r["state"] == "outcomeUnknown" for r in records) or mode.startswith("host.") and any(r["state"] in ("checkpointIntent", "dispatchIntent", "recoveryIntent") for r in records), "Lost native response must retain an uncertain durable intent.")
    base.replay_refusal_check(root, operation)
    refusal = load(root / "competing-owner-refusal.json")
    need(refusal["code"] == "ResourceBusy" and refusal["operationId"] == operation["id"] and refusal["competitorId"] != operation["id"], "Competing owner refusal is missing or unbound.")
    competitor = journal(root, refusal["competitorId"])
    need(all(r["state"] == "prepared" for r in competitor) and competitor[0]["plan"]["resources"] == records[0]["plan"]["resources"], "Competing owner acquired resources or used unrelated resources.")
    ending = load(root / "matrix-reconciliation-result.json")
    need(ending["completed"] is True and ending["operationId"] == operation["id"] and ending["automaticReplay"] is False and ending["hostProcessId"] == refusal["hostProcessId"], "Matrix completion/competitor Host identity differs.")
    need(type(ending["hostProcessId"]) is int and ending["hostProcessId"] > 0, "Positive reconciliation Host process identity required.")
    initial_host = marker.get("hostProcessId", admission.get("hostProcessId"))
    if initial_host is not None: need(type(initial_host) is int and initial_host > 0, "Positive initial Host process identity required.")
    restarted = initial_host is not None and initial_host != ending["hostProcessId"]
    if initial_host is not None: need(restarted, "Host process was reused despite restart claim.")
    if records[-1]["recoveryDestinations"] and mode in {"retrieve.before", "retrieve.after", "host.watch_restore_native"}:
        refusal = load(root / "retrieve-replay-refusal.json")
        need(refusal["code"] == "RecoveryRequired" and refusal["nativeRetrieveInvoked"] is False, "Uncertain retrieval replay was not explicitly refused.")
    if mode.startswith("checkpoint.") or mode == "host.watch_checkpoint":
        need(not any(r["state"] == "dispatchIntent" for r in records) and not records[-1]["checkpoints"], "Lost checkpoint response must not dispatch or silently adopt an orphan archive.")
    else: need(any(r["state"] == "dispatchIntent" for r in records), "Import/retrieval interruption has no durable mutation intent.")
    if mode in NATIVE_POINTS:
        need(marker["workerSha256"] == admission["workerSha256"] and type(marker["workerProcessId"]) is int and marker["workerProcessId"] > 0 and type(marker["processId"]) is int and marker["processId"] > 0, "Interrupted worker/native identity is unbound.")
        if mode.startswith("retrieve."):
            need(len(records[-1]["recoveryDestinations"]) == 1 and marker["output"] == records[-1]["recoveryDestinations"][0]["recoveryLocation"], "Retrieval marker has a different admitted destination.")
            need(marker["projectPath"] is None if mode == "retrieve.before" else
                 Path(marker["projectPath"]).resolve().is_relative_to(Path(marker["output"]).resolve()), "Retrieval marker project is outside its boundary.")
        else:
            need(marker["projectPath"] == operation["projectPath"], "Interrupted native call belongs to another project.")
            if mode == "checkpoint.afterArchive":
                need(Path(marker["output"]).is_file() and Path(marker["output"]).resolve().is_relative_to(root.resolve()) and Path(marker["output"]).suffix.lower() == ".zap21", "Lost checkpoint response requires its retained, unadopted archive.")
            else: need(marker["output"] is None, "Unexpected native output at the marked interruption boundary.")
    else:
        response = marker.get("response"); need(isinstance(response, dict) and response, "Host interruption requires the retained native response.")
        if mode == "host.watch_checkpoint":
            artifact(root, response["checkpoint"])
            need(response["snapshot"]["project"]["path"] == operation["projectPath"] and response["snapshot"]["observation"]["offline"] is True, "Lost checkpoint response is unbound.")
        elif mode == "host.watch_apply_native":
            need(response["project"]["path"] == operation["projectPath"] and response["saved"] is True and response["automaticReplay"] is False, "Lost apply response is not the saved exact target.")
            artifact(root, response["target"]["artifact"]); artifact(root, response["receipt"])
        else:
            need(len(records[-1]["recoveryDestinations"]) == 1 and response["destination"] == records[-1]["recoveryDestinations"][0]["recoveryLocation"] and
                 Path(response["project"]["path"]).resolve().is_relative_to(Path(response["destination"]).resolve()), "Lost retrieval response escapes admitted destination.")
    lifecycle = actual_host_lifecycle(root, admission, True) if admission.get("actualMcpHost") is True else None
    return {"point": mode, "hostRestartVerified": restarted, "hostRestartEvidence": "exactClosedHostAndOwnedProcessHandlesBeforeRestart" if lifecycle else "distinctRetainedHostPids" if restarted else "initialHostPidNotRetained",
            "actualHostLifecycle": lifecycle,
            "competingOwnerRefused": True, "automaticReplay": False, "marker": ref(root / "interruption-marker.json")}


def actual_host_lifecycle(root, admission, interrupted):
    """Require actual stdio process lifetimes; PID assertions alone are not restart proof."""
    root = Path(root)
    need(admission.get("actualMcpHost") is True, "Exact Host lifecycle gate requires actualMcpHost admission.")
    for key in ("host", "worker"):
        path = Path(admission[key])
        need(path.is_file() and sha(path.read_bytes()) == admission[key + "Sha256"], "Admitted " + key + " binary bytes changed.")
    count = 2 if interrupted else 1
    expected_starts = {f"host-{i:02}-start.json" for i in range(1, count + 1)}
    expected_closes = {f"host-{i:02}-close.json" for i in range(1, count + 1)}
    need({p.name for p in root.glob("host-*-start.json")} == expected_starts and
         {p.name for p in root.glob("host-*-close.json")} == expected_closes, "Exact complete Host start/close inventory required.")
    starts, closes, close_packets, owners, native_ids, all_identities = [], [], [], [], set(), set()
    def process_identity(value):
        need(isinstance(value, dict) and type(value.get("pid")) is int and value["pid"] > 0 and
             type(value.get("parentPid")) is int and value["parentPid"] > 0 and
             type(value.get("created")) in (int, float) and math.isfinite(value["created"]) and value["created"] > 0 and
             isinstance(value.get("name"), str) and value["name"] and isinstance(value.get("exe"), str) and value["exe"], "Exact process identity is missing or invalid.")
        identity = value["pid"], value["created"]
        need(identity not in all_identities, "A captured process identity was reused across lifetimes.")
        all_identities.add(identity)
        return identity
    for index in range(1, count + 1):
        started = load(root / f"host-{index:02}-start.json")
        closed = load(root / f"host-{index:02}-close.json")
        host = started["identity"]; process_identity(host)
        need(Path(host["exe"]).resolve() == Path(admission["host"]).resolve(), "Host executable differs from admission.")
        need(closed.get("identity") == host, "Host close identity differs from its captured start.")
        closed_at = closed.get("closedAt")
        need(type(closed_at) in (int, float) and math.isfinite(closed_at) and closed_at >= host["created"], "Bounded Host closedAt proof is missing or predates its start.")
        fault = interrupted and index == 1
        worker_fault = fault and admission["mode"] in NATIVE_POINTS
        host_fault = fault and admission["mode"] in HOST_POINTS
        expected_host_exit = 74 if host_fault else 1 if worker_fault else 0
        expected_worker_exit = 73 if worker_fault else 0
        need(started.get("point") == (admission["mode"] if fault else "none") and
             closed.get("expectedLoss") is fault, "Host phase/interruption admission differs.")
        close = closed["close"]
        need(close.get("forcedHostStop") is False and close.get("hostExitCode") == expected_host_exit,
             "Host exit is forced, missing or different from the admitted boundary.")
        need(close.get("stderrEofObserved") is True, "Host stderr EOF/exit receipt is incomplete.")
        exits = closed.get("exits")
        need(isinstance(exits, list) and exits, "Owned process handle-exit inventory is empty.")
        monitored = load(root / f"host-{index:02}-processes.json")
        need(isinstance(monitored, list) and monitored == [entry["identity"] for entry in exits], "Monitored owned process inventory differs from close proofs.")
        workers = [entry for entry in exits if Path(entry["identity"]["exe"]).resolve() == Path(admission["worker"]).resolve()]
        need(len(workers) == 1, "Exactly one admitted worker identity and handle proof are required per Host.")
        worker = workers[0]["identity"]
        need(worker["parentPid"] == host["pid"] and worker["created"] >= host["created"], "Admitted worker is not a child of this Host lifetime.")
        natives = []
        for entry in exits:
            info, proof = entry["identity"], entry["proof"]; process_identity(info)
            need(proof.get("processId") == info["pid"] and proof.get("completed") is True and
                 proof.get("processExitVerified") is True and proof.get("handleCaptured") is True and proof.get("handleClosed") is True and
                 type(proof.get("exitCode")) is int, "Exact captured process handle exit proof is incomplete or mismatched.")
            need(host["created"] <= info["created"] <= closed_at, "Owned process identity is outside its Host lifetime.")
            if entry is workers[0]:
                need(proof["exitCode"] == expected_worker_exit, "Worker exit differs from the admitted interruption boundary.")
            else:
                need(info["name"] == "Siemens.Automation.Portal.exe" and info["parentPid"] == worker["pid"] and
                     info["created"] >= worker["created"], "Unexpected or unrelated owned process in cleanup receipt.")
                natives.append(info); native_ids.add(info["pid"])
        need(natives, "No exact owned native process cleanup was retained.")
        receipts = close.get("workerExits")
        need(isinstance(receipts, list) and (host_fault or len(receipts) == 1), "Normal/worker-fault Host must retain one exact worker shutdown receipt.")
        need(len(receipts) <= 1, "Unrelated worker shutdown receipts appeared.")
        for receipt in receipts:
            need(receipt.get("processId") == worker["pid"] and receipt.get("processExitVerified") is True and
                 receipt.get("exitCode") == expected_worker_exit and receipt.get("forcedTermination") is False,
                 "Worker shutdown receipt differs from the exact monitor proof.")
            timestamp = receipt.get("startedAtUtc")
            need(isinstance(timestamp, str) and abs(datetime.fromisoformat(timestamp.replace("Z", "+00:00")).timestamp() - worker["created"]) <= 0.01,
                 "Worker shutdown receipt belongs to another process creation time.")
        starts.append(host); closes.append(closed_at); close_packets.append(closed); owners.append((worker, natives))
    need(starts[0]["pid"] == admission["hostProcessId"], "Initial Host identity differs from native admission.")
    if interrupted:
        need(starts[0]["pid"] != starts[1]["pid"] and closes[0] <= starts[1]["created"], "Original Host was not proved closed before the fresh Host started.")
        marker = load(root / "interruption-marker.json")
        ending = load(root / "matrix-reconciliation-result.json")
        refusal = load(root / "competing-owner-refusal.json")
        need(ending["hostProcessId"] == refusal["hostProcessId"] == starts[1]["pid"], "Recovery/competitor did not use the retained restarted Host.")
        if admission["mode"] in HOST_POINTS:
            timestamp = marker.get("hostStartedAtUtc")
            need(marker.get("hostProcessId") == starts[0]["pid"] and isinstance(timestamp, str) and
                 abs(datetime.fromisoformat(timestamp.replace("Z", "+00:00")).timestamp() - starts[0]["created"]) <= 0.01,
                 "Host interruption marker belongs to another exact process lifetime.")
        else:
            need(marker.get("workerProcessId") == owners[0][0]["pid"] and
                 marker.get("processId") in {p["pid"] for p in owners[0][1]}, "Worker interruption marker belongs to another exact owned process.")
    if (root / "review.json").is_file():
        need(load(root / "review.json")["review"]["before"]["processId"] in native_ids, "Reviewed native baseline lacks exact cleanup evidence.")
    cleanup = coordinator_native_cleanup(root, admission, interrupted, starts, close_packets, owners)
    result = {"hostCount": count, "exactWorkerLifetimes": count, "exactNativeLifetimes": len(native_ids),
            "initialHostProcessId": starts[0]["pid"], "finalHostProcessId": starts[-1]["pid"],
            "closedBeforeRestart": interrupted, "coordinatorInterventionRequired": cleanup is not None,
            "normalLifecyclePassed": not interrupted, "explicitNativeTermination": cleanup is not None,
            "coordinatorNativeCleanup": cleanup,
            "receipts": [ref(root / f"host-{index:02}-close.json") for index in range(1, count + 1)]}
    if cleanup is None:
        result["forcedTermination"] = False
    return result


def coordinator_native_cleanup(root, admission, interrupted, hosts, close_packets, owners):
    """Explicit owned-process termination is an intervention, never normal lifecycle proof."""
    intent_path = root / "coordinator-native-close-intent.json"
    result_path = root / "coordinator-native-close.json"
    if not intent_path.exists() and not result_path.exists():
        return None
    need(interrupted and admission["mode"] in NATIVE_POINTS, "Coordinator native cleanup is allowed only for an admitted worker interruption.")
    need(intent_path.is_file() and result_path.is_file(), "Both immutable coordinator cleanup intent and return are required.")
    helper = admission.get("nativeCleanupHelper")
    need(isinstance(helper, dict) and isinstance(helper.get("sources"), list) and 1 <= len(helper["sources"]) <= 32,
         "Admitted cleanup helper binary and source hashes are required.")
    paths = set()
    for item in [helper, *helper["sources"]]:
        need(isinstance(item, dict) and isinstance(item.get("path"), str) and isinstance(item.get("sha256"), str), "Exact helper artifact binding required.")
        path = Path(item["path"]).resolve()
        need(path not in paths and path.is_file() and sha(path.read_bytes()) == item["sha256"], "Cleanup helper/source bytes changed, are absent or duplicated.")
        paths.add(path)
    intent, returned = load(intent_path), load(result_path)
    required = {"identity", "markerSha256", "helperSha256", "projectPath", "method", "explicitNativeTermination",
                "automaticReplay", "requestedAtUtc", "sameOsHandleRetained"}
    os_termination = intent.get("method") == "TerminateProcess(exactOwnedHandle)"
    if os_termination:
        required.add("projectIdentityBasis")
        need(helper.get("method") == intent["method"] and intent["projectIdentityBasis"] == "Durable interruption marker from the exact admitted worker, not a new native attachment",
             "OS cleanup method and marker-only project identity basis must be explicitly admitted.")
        need(returned.get("exitCode") == 75, "Explicit OS cleanup terminal code differs.")
    need(set(intent) == required and set(returned) == required | {"completed", "exitCode", "closedAtUtc"}, "Cleanup intent/return fields are incomplete or unreviewed.")
    need(all(returned[k] == value for k, value in intent.items()), "Cleanup return changed the immutable intent.")
    need((intent["method"] == "TiaPortalProcess.Dispose" or os_termination) and intent["explicitNativeTermination"] is True and
         intent["automaticReplay"] is False and intent["sameOsHandleRetained"] is True,
         "Cleanup must declare exact explicit termination, no replay and its retained helper handle.")
    need(returned["completed"] is True and type(returned["exitCode"]) is int, "Cleanup helper did not prove terminal native exit.")
    marker = load(root / "interruption-marker.json")
    need(intent["markerSha256"] == sha((root / "interruption-marker.json").read_bytes()) and
         intent["helperSha256"] == helper["sha256"] and marker.get("workerSha256") == admission["workerSha256"],
         "Cleanup marker/helper/worker hashes differ from admission.")
    first = close_packets[0]
    native = [entry for entry in first["exits"] if entry["identity"]["pid"] == marker.get("processId")]
    need(len(native) == 1 and intent["identity"] == native[0]["identity"] and
         native[0]["identity"] in owners[0][1] and marker.get("workerProcessId") == owners[0][0]["pid"],
         "Cleanup is not bound to the original interrupted worker's exact native lifetime.")
    need(intent["projectPath"] == marker.get("projectPath"), "Cleanup observed another project than the interruption marker.")
    proof = native[0]["proof"]
    need(proof.get("completed") is True and proof.get("processExitVerified") is True and proof.get("handleCaptured") is True and
         proof.get("handleClosed") is True and proof.get("exitCode") == returned["exitCode"],
         "Original native monitor handle did not independently prove the same terminal exit.")
    def epoch(value):
        need(isinstance(value, str), "Timestamped cleanup and original-handle capture are required.")
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        need(parsed.tzinfo is not None, "Cleanup timestamps must carry an explicit timezone.")
        result = parsed.timestamp(); need(math.isfinite(result), "Cleanup timestamp is not finite.")
        return result
    requested, completed = epoch(intent["requestedAtUtc"]), epoch(returned["closedAtUtc"])
    captured = epoch(proof.get("capturedAtUtc"))
    close_requested = first.get("closeRequestedAt")
    need(type(close_requested) in (int, float) and math.isfinite(close_requested), "Host close request time is required for intervention ordering.")
    need(hosts[0]["created"] <= native[0]["identity"]["created"] <= captured <= requested <= completed <= close_requested <= first["closedAt"] <= hosts[1]["created"],
         "Original monitor capture, explicit cleanup, Host close and restart order is invalid.")
    return {"method": intent["method"], "explicitNativeTermination": True, "coordinatorInterventionRequired": True,
            "normalLifecyclePassed": False, "processId": native[0]["identity"]["pid"], "exitCode": returned["exitCode"],
            "originalMonitorHandleExitVerified": True, "requestedAtUtc": intent["requestedAtUtc"], "closedAtUtc": returned["closedAtUtc"],
            "intent": ref(intent_path), "result": ref(result_path), "helper": helper}


def verify(run_root):
    root = Path(run_root).resolve(); issues, stages = [], []
    result = {"kind": "independent-watch-expansion-verification", "root": str(root), "nativeInvoked": False,
              "verifiedAtUtc": datetime.now(timezone.utc).isoformat(), "stages": stages, "issues": issues}
    def check(name, fn):
        try:
            value = fn(); stages.append({"name": name, "passed": True, "detail": value}); return value
        except (VerificationError, OSError, ValueError, KeyError, IndexError, TypeError, ET.ParseError) as error:
            issues.append({"stage": name, "error": str(error)}); stages.append({"name": name, "passed": False}); return None
    reviewed = check("reviewedExactDocumentDelta", lambda: reviewed_candidate(root))
    if reviewed is None: result["completed"] = False; return result
    packets = check("nativeRunIdentity", lambda: [load(root / "review.json"), load(root / "operation.json"), load(root / "admission.json")])
    if packets is None: result["completed"] = False; return result
    outer, operation, admission = packets
    stages[-1]["detail"] = {"operationId": operation["id"], "mode": admission["mode"]}
    review, before = outer["review"], outer["review"]["before"]
    baseline, candidate = Path(reviewed["baseline"]["path"]).read_bytes(), Path(reviewed["candidate"]["path"]).read_bytes()
    records = check("durableJournal", lambda: journal(root, operation["id"]))
    if records is None: result["completed"] = False; return result
    stages[-1]["detail"] = {"records": len(records), "states": [r["state"] for r in records]}
    def approval():
        approved = load(root / "independent-review.json")
        need(approved["approved"] is True and approved["noOnlineAction"] is True and approved["operationId"] == operation["id"] == reviewed["operationId"], "Exact independent approval missing.")
        need(approved["reviewedPlanSha256"] == reviewed["reviewedPlanSha256"] == operation["digest"] == (root / "approved-digest").read_text().strip(), "Approved plan digest differs.")
        need(base.reviewed_input_digest(approved) == reviewed["reviewedInputSha256"] and records[0] == outer["operation"], "Approved candidate or prepared journal differs.")
        need(operation["projectPath"] == before["project"]["path"], "Approved original project differs.")
        return ref(root / "independent-review.json")
    check("independentApproval", approval)
    interrupted = admission["mode"] in NATIVE_POINTS | HOST_POINTS
    if admission.get("actualMcpHost") is True and not interrupted:
        result["actualHostLifecycle"] = check("exactHostWorkerAndNativeCleanup", lambda: actual_host_lifecycle(root, admission, False))
    result["edit"] = reviewed["delta"]
    if not interrupted or (root / "applied.json").is_file():
        def applied():
            packet = load(root / "applied.json"); snap = packet["applied"]
            snapshot_check(root, snap, candidate, before, project_path=operation["projectPath"])
            need(snap["saved"] is True and snap["automaticReplay"] is False, "Saved native apply/no-replay evidence missing.")
            need(load(artifact(root, packet["evidence"])) == snap, "Retained apply response differs.")
            return {"saved": True, "completeXmlAndPeersVerified": True}
        check("nativeAppliedReadbackAndPeers", applied)
    if not interrupted or (root / "verified.json").is_file():
        def reopened():
            snap = load(root / "verified.json")["reopened"]
            snapshot_check(root, snap, candidate, before, True, operation["projectPath"])
            return {"processId": snap["processId"], "sessionEpoch": snap["observation"]["sessionEpoch"]}
        check("freshReopen", reopened)
    if interrupted: result["interruption"] = check("interruptionRestartCompetingOwnersAndNoReplay", lambda: interruption_check(root, admission, records, operation))
    result["recovery"] = check("freshOriginalRestoredAndReconciliation", lambda: recovery_check(root, records, review, operation, baseline, candidate, interrupted))
    if not interrupted:
        def completed():
            ending = load(root / "result.json")
            need(ending["completed"] is True and ending["id"] == operation["id"] and ending["nativeOnline"] is False and ending["projectPath"] == operation["projectPath"] and
                 result["recovery"] is not None and ending["recoveryPath"] == result["recovery"]["recoveryPath"], "Final native workflow result is unbound.")
            need(not (root / "failure.json").exists(), "Native failure must not become a successful workflow.")
            return ref(root / "result.json")
        check("nativeWorkflowCompleted", completed)
    if (root / "failure.json").is_file(): result["retainedFailure"] = ref(root / "failure.json")
    result["completed"] = not issues
    result["qualificationPassed"] = not issues and not interrupted
    result["hostRestartVerified"] = bool(result.get("interruption") and result["interruption"]["hostRestartVerified"])
    result["interruptionMatrixCasePassed"] = not issues and interrupted and result["hostRestartVerified"]
    lifecycle = result.get("actualHostLifecycle") or (result.get("interruption") or {}).get("actualHostLifecycle")
    if lifecycle is not None:
        for field in ("coordinatorInterventionRequired", "normalLifecyclePassed", "explicitNativeTermination"):
            result[field] = lifecycle[field]
    result["coverageGaps"] = ["Initial and restarted Host process identities are not independently retained."] if interrupted and not result["hostRestartVerified"] else []
    return result


def main():
    parser = argparse.ArgumentParser(); parser.add_argument("run_root"); parser.add_argument("--output", required=True); parser.add_argument("--review-only", action="store_true")
    args = parser.parse_args(); output = Path(args.output); need(not output.exists(), "Use a fresh verification output path.")
    try:
        result = dict(reviewed_candidate(args.run_root), completed=True, reviewOnly=True, approved=False) if args.review_only else verify(args.run_root)
    except (VerificationError, OSError, ValueError, KeyError, IndexError, TypeError, ET.ParseError, expat.ExpatError) as error:
        result = {"completed": False, "reviewOnly": args.review_only, "nativeInvoked": False, "error": str(error)}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="\n") as stream: json.dump(result, stream, ensure_ascii=False, indent=2); stream.write("\n")
    print(json.dumps({"completed": result["completed"], "issues": result.get("issues", result.get("error")), "output": str(output)}, ensure_ascii=True))
    return 0 if result["completed"] else 1


if __name__ == "__main__": raise SystemExit(main())
