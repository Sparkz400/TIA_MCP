"""Portable adversarial checks of the independent WT expansion oracle.

Derived documents are authored test variants of genuine captured grammar, never
claimed to be new native exports. No test invokes TIA or imports production code.
"""
import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

import verify_watch_expansion as oracle
import verify_watch_workflow as base

FIXTURE = Path(__file__).resolve().parents[1] / "fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml"


def raw(tree): return ET.tostring(tree, encoding="utf-8", xml_declaration=True)


def row_cloned(row, first_id):
    cloned = copy.deepcopy(row)
    for item in cloned.iter():
        if "ID" in item.attrib: item.attrib["ID"] = format(first_id, "X"); first_id += 1
    cloned.tail = None
    return cloned


def fixture(rows=3, opaque=True):
    tree = oracle.document(FIXTURE.read_bytes()); objects = base.table(tree).find("ObjectList"); first = oracle.row_elements(tree)[0]
    for index in range(1, rows): objects.append(row_cloned(first, index * 16))
    for index, row in enumerate(oracle.row_elements(tree)):
        row.find("AttributeList/ModifyValue").text = "TRUE" if index == 1 else "FALSE"
        for text in row.findall(".//MultilingualTextItem/AttributeList/Text"): text.text = (text.text or "") + " row=" + str(index)
    if opaque:
        field = ET.SubElement(first.find("AttributeList"), "Opaque", {"future": "kept"}); field.text = "  "
        field.append(ET.Comment("retain exact unknown comment")); field[-1].tail = "\tunknown  "
    return tree


def comment(tree, culture, text, ordinal=0):
    row = oracle.row_elements(tree)[ordinal]
    for item in row.findall(".//MultilingualTextItem/AttributeList"):
        if item.findtext("Culture") == culture: item.find("Text").text = text


def reorder(tree, order):
    objects = base.table(tree).find("ObjectList"); rows = oracle.row_elements(tree)
    replacements = [copy.deepcopy(rows[i]) for i in order]
    for row, replacement in zip(rows, replacements):
        index = list(objects).index(row); replacement.tail = row.tail; objects.remove(row); objects.insert(index, replacement)


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True); path.write_text(json.dumps(data), encoding="utf-8")


def save_records(root, states, operation="owned", plan=None):
    plan = plan or {"operation": "watchDocumentEdit", "inputs": [], "resources": [{"resourceKey": "project:OWNED", "kind": "project"}]}
    records = []
    for index, state in enumerate(states):
        records.append({"id": operation, "revision": index, "state": state, "plan": plan, "planSha256": base.canonical_sha(plan),
            "previousSha256": base.canonical_sha(records[-1]) if records else None, "checkpoints": [], "evidence": [], "reconciliation": [], "recoveryDestinations": [], "recoveryResolutions": []})
        write(root / "engineering-operations" / operation / f"{index:06}.json", records[-1])
    return records


def saved_json(root, name, value):
    path = root / name; write(path, value)
    return {"relativePath": name, "sha256": oracle.sha(path.read_bytes())}


def snapshot(root, name, data, process, project):
    directory = root / name; directory.mkdir()
    path = directory / "watch-0.xml"; path.write_bytes(data); tree = oracle.document(data)
    rows = []
    for row in oracle.row_elements(tree):
        rows.append(dict(kind="PlcWatchTableEntry", name=row.findtext("AttributeList/Name"), address=row.findtext("AttributeList/Address", ""),
            displayFormat="Bool", modifyValue=row.findtext("AttributeList/ModifyValue"), modifyIntention=False))
    target = {"kind": "watch", "group": "root", "name": "MilestoneWatch", "isConsistent": True, "rows": rows,
              "artifact": ref_path(path), "semanticSha256": "a" * 64}
    value = {"target": target, "tables": [copy.deepcopy(target)], "observation": {"offline": True, "projectFilePath": project,
        "reference": {"contentSha256": "a" * 64}, "sessionEpoch": "native-" + str(process), "adapterSha256": "b" * 64,
        "profileSha256": "c" * 64, "hardwareIdentity": "owned-hardware", "nativeIdentity": None,
        "probes": [{"name": name, "state": "observed"} for name in ("offline", "watchImport")]},
        "processId": process, "projectModified": False, "project": {"path": project}, "peers": {}, "peerSha256": "d" * 64}
    receipt = directory / "snapshot.json"; write(receipt, value); value["receipt"] = ref_path(receipt)
    return value


class WatchExpansionOracleTests(unittest.TestCase):
    def test_genuine_fixture_and_all_three_reviewed_comment_cultures(self):
        self.assertEqual(1, len(oracle.row_elements(oracle.document(FIXTURE.read_bytes()))))
        old = fixture()
        for culture, value in [("de-DE", "Freigabe geändert Ω {0}"), ("en-GB", "Enabled\nexact <&> {0}"), ("nl-NL", "Schakelstatus gewijzigd 日本語 😀")]:
            new = copy.deepcopy(old); comment(new, culture, value)
            result = oracle.exact_delta(raw(old), raw(new), {"kind": "comment", "ordinal": 0, "culture": culture, "text": value})
            self.assertEqual("comment", result["kind"]); self.assertTrue(result["unknownPayloadPreserved"])

    def test_comment_refuses_changed_peers_languages_ids_or_unknown_payload(self):
        old = fixture(); edited = copy.deepcopy(old); comment(edited, "de-DE", "reviewed")
        edit = {"kind": "comment", "ordinal": 0, "culture": "de-DE", "text": "reviewed"}
        variants = []
        new = copy.deepcopy(edited); comment(new, "en-GB", "unreviewed"); variants.append(new)
        new = copy.deepcopy(edited); comment(new, "de-DE", "peer changed", 1); variants.append(new)
        new = copy.deepcopy(edited); new.find(".//Opaque").text = " "; variants.append(new)
        new = copy.deepcopy(edited); oracle.row_elements(new)[1].attrib["ID"] = "FF"; variants.append(new)
        new = copy.deepcopy(edited); new.find("DocumentInfo/Created").text = "2026-10-16T00:00:00Z"; variants.append(new)
        new = copy.deepcopy(edited); items = oracle.row_elements(new)[0].find("ObjectList/MultilingualText/ObjectList"); items.remove(items[-1]); variants.append(new)
        for new in variants:
            with self.subTest(variant=len(raw(new))), self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(new), edit)

    def test_comment_unknown_or_duplicate_culture_refused(self):
        old = fixture(); new = copy.deepcopy(old); comment(new, "de-DE", "new")
        for culture in ("DE-de", "fr-FR"):
            with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(new), {"kind": "comment", "ordinal": 0, "culture": culture, "text": "new"})
        old.findall(".//Culture")[1].text = "de-DE"
        with self.assertRaisesRegex(oracle.VerificationError, "Duplicate comment culture"): oracle.document(raw(old))

    def test_noop_unknown_fields_and_boolean_ordinals_refused(self):
        old = fixture(); new = copy.deepcopy(old); comment(new, "de-DE", "new")
        good = {"kind": "comment", "ordinal": 0, "culture": "de-DE", "text": "new"}
        for edit in [dict(good, ordinal=True), dict(good, bypass=True), dict(good, ordinal=-1), dict(good, ordinal=3)]:
            with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(new), edit)
        original_text = old.find(".//MultilingualTextItem/AttributeList/Text").text
        with self.assertRaisesRegex(oracle.VerificationError, "no change"): oracle.exact_delta(raw(old), raw(old), dict(good, text=original_text))

    def test_unknown_prolog_and_epilog_nodes_are_not_discarded(self):
        old = fixture(); new = copy.deepcopy(old); comment(new, "de-DE", "reviewed")
        edit = {"kind": "comment", "ordinal": 0, "culture": "de-DE", "text": "reviewed"}
        before = raw(old).replace(b"<Document>", b"<?opaque keep?><!--source--><Document>") + b"<!--retained-tail-->"
        candidate = raw(new).replace(b"<Document>", b"<?opaque keep?><!--source--><Document>") + b"<!--retained-tail-->"
        oracle.exact_delta(before, candidate, edit)
        for changed in (candidate.replace(b"opaque keep", b"opaque lost"), candidate.replace(b"<!--source-->", b""), candidate.replace(b"retained-tail", b"different-tail")):
            with self.assertRaisesRegex(oracle.VerificationError, "prolog/epilog"): oracle.exact_delta(before, changed, edit)

    def test_clone_accepts_only_fresh_ids_and_complete_opaque_payload(self):
        old = fixture(); new = copy.deepcopy(old); objects = base.table(new).find("ObjectList")
        objects.append(row_cloned(oracle.row_elements(new)[0], 128))
        result = oracle.exact_delta(raw(old), raw(new), {"kind": "clone", "ordinal": 0, "insertAt": 3})
        self.assertEqual(4, result["candidateRows"])
        for value in ("1", "01", "10"):
            changed = copy.deepcopy(new); oracle.row_elements(changed)[3].attrib["ID"] = value
            with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(changed), {"kind": "clone", "ordinal": 0, "insertAt": 3})
        changed = copy.deepcopy(new); oracle.row_elements(changed)[3].find("AttributeList/Opaque").attrib["future"] = "lost"
        with self.assertRaisesRegex(oracle.VerificationError, "Clone changed full payload"): oracle.exact_delta(raw(old), raw(changed), {"kind": "clone", "ordinal": 0, "insertAt": 3})

    def test_clone_cannot_renumber_unchanged_rows_or_copy_id_references(self):
        old = fixture(); new = copy.deepcopy(old); base.table(new).find("ObjectList").append(row_cloned(oracle.row_elements(new)[0], 128))
        oracle.row_elements(new)[1].attrib["ID"] = "AA"
        with self.assertRaisesRegex(oracle.VerificationError, "beyond the exact"): oracle.exact_delta(raw(old), raw(new), {"kind": "clone", "ordinal": 0, "insertAt": 3})
        old = fixture(); oracle.row_elements(old)[0].find("AttributeList/Opaque").attrib["TargetRef"] = "1"
        new = copy.deepcopy(old); base.table(new).find("ObjectList").append(row_cloned(oracle.row_elements(new)[0], 128))
        with self.assertRaisesRegex(oracle.VerificationError, "Referenced row payload"): oracle.exact_delta(raw(old), raw(new), {"kind": "clone", "ordinal": 0, "insertAt": 3})

    def test_clone_exact_insertion_position(self):
        old = fixture(); new = copy.deepcopy(old); objects = base.table(new).find("ObjectList"); objects.insert(1, row_cloned(oracle.row_elements(new)[2], 128))
        oracle.exact_delta(raw(old), raw(new), {"kind": "clone", "ordinal": 2, "insertAt": 1})
        with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(new), {"kind": "clone", "ordinal": 2, "insertAt": 0})

    def test_reorder_preserves_ids_payload_and_interrow_comment_slots(self):
        old = fixture(); objects = base.table(old).find("ObjectList"); objects.insert(1, ET.Comment("between row slots")); objects.insert(3, ET.ProcessingInstruction("opaque", "preserved"))
        new = copy.deepcopy(old); reorder(new, [2, 0, 1])
        oracle.exact_delta(raw(old), raw(new), {"kind": "reorder", "order": [2, 0, 1]})
        for order in ([2, 0, 0], [2, 0], [2, 0, True]):
            with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(new), {"kind": "reorder", "order": order})
        changed = copy.deepcopy(new); base.table(changed).find("ObjectList")[1].text = "moved/changed"
        with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(changed), {"kind": "reorder", "order": [2, 0, 1]})
        changed = copy.deepcopy(new); oracle.row_elements(changed)[0].attrib["ID"] = "AA"
        with self.assertRaisesRegex(oracle.VerificationError, "beyond the exact"): oracle.exact_delta(raw(old), raw(changed), {"kind": "reorder", "order": [2, 0, 1]})

    def test_delete_preserves_remaining_ids_and_unknown_interrow_text(self):
        old = fixture(); objects = base.table(old).find("ObjectList"); objects.insert(1, ET.Comment("keep slot")); selected = oracle.row_elements(old)[1]; selected.tail = "unknown interrow text"
        new = copy.deepcopy(old); objects = base.table(new).find("ObjectList"); row = oracle.row_elements(new)[1]
        previous = list(objects)[list(objects).index(row) - 1]; previous.tail = (previous.tail or "") + row.tail; objects.remove(row)
        oracle.exact_delta(raw(old), raw(new), {"kind": "delete", "ordinal": 1})
        changed = copy.deepcopy(new); base.table(changed).find("ObjectList")[1].tail = None
        with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(changed), {"kind": "delete", "ordinal": 1})
        changed = copy.deepcopy(new); oracle.row_elements(changed)[1].attrib["ID"] = "AA"
        with self.assertRaises(oracle.VerificationError): oracle.exact_delta(raw(old), raw(changed), {"kind": "delete", "ordinal": 1})

    def test_original_disposition_never_guesses_from_interruption_point(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); old = fixture(); new = copy.deepcopy(old); comment(new, "en-GB", "changed")
            path = root / "target.xml"
            for tree, expected in [(old, "unchanged"), (new, "verifiedApplied")]:
                path.write_bytes(raw(tree)); snapshot = {"target": {"artifact": ref_path(path)}}
                self.assertEqual(expected, oracle.original_disposition(root, snapshot, raw(old), raw(new))[0])
            bad = copy.deepcopy(new); oracle.row_elements(bad)[0].attrib["ID"] = "AA"; path.write_bytes(raw(bad))
            with self.assertRaises(oracle.VerificationError): oracle.original_disposition(root, {"target": {"artifact": ref_path(path)}}, raw(old), raw(new))

    def test_typed_row_order_field_and_modify_intention_independent(self):
        tree = fixture(); rows = []
        for row in oracle.row_elements(tree):
            rows.append(dict(kind="PlcWatchTableEntry", name=row.findtext("AttributeList/Name"), address=row.findtext("AttributeList/Address", ""),
                displayFormat="Bool", modifyValue=row.findtext("AttributeList/ModifyValue"), modifyIntention=False))
        snapshot = {"target": {"kind": "watch", "name": "MilestoneWatch", "isConsistent": True, "rows": rows}}
        oracle.typed_rows(snapshot, tree)
        for key, value in [("name", "wrong"), ("address", "%M0.0"), ("displayFormat", "Hex"), ("modifyValue", "TRUE"), ("modifyIntention", True)]:
            changed = copy.deepcopy(snapshot); changed["target"]["rows"][0][key] = value
            with self.assertRaises(oracle.VerificationError): oracle.typed_rows(changed, tree)
        changed = copy.deepcopy(snapshot); changed["target"]["rows"] = rows[1:] + rows[:1]
        with self.assertRaises(oracle.VerificationError): oracle.typed_rows(changed, tree)

    def test_no_archive_checkpoint_journal_allowed_without_fake_dispatch(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); records = save_records(root, ["prepared", "acquired", "checkpointIntent", "outcomeUnknown", "reconciled"])
            self.assertEqual(records, oracle.journal(root, "owned"))
            changed = copy.deepcopy(records[2]); changed["state"] = "ready"; write(root / "engineering-operations/owned/000002.json", changed)
            with self.assertRaisesRegex(oracle.VerificationError, "hash chain"): oracle.journal(root, "owned")

    def test_replayed_checkpoint_dispatch_and_retrieval_intents_rejected(self):
        for intent in ("checkpointIntent", "dispatchIntent", "recoveryIntent"):
            with self.subTest(intent=intent), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); save_records(root, ["prepared", intent, "outcomeUnknown", intent])
                with self.assertRaisesRegex(oracle.VerificationError, "replayed"): oracle.journal(root, "owned")

    def test_recovery_directory_manifest_is_exact_and_never_a_restoration_claim(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); destination = root / "recovery"
            manifest = {"destination": str(destination), "exists": False, "directories": [], "files": [], "totalBytes": 0,
                "scope": "retainedRegularFileBytesOnly", "restoredProjectVerified": False}
            oracle.destination_manifest(root, manifest, "verifiedAbsent", str(destination))
            destination.mkdir(); file = destination / "partial.bin"; file.write_bytes(b"retained")
            manifest.update(exists=True, files=[{"relativePath": "partial.bin", "sha256": oracle.sha(b"retained"), "bytes": 8}], totalBytes=8)
            oracle.destination_manifest(root, manifest, "retainedPartial", str(destination))
            with self.assertRaises(oracle.VerificationError): oracle.destination_manifest(root, manifest, "verifiedAbsent", str(destination))
            with self.assertRaises(oracle.VerificationError): oracle.destination_manifest(root, dict(manifest, restoredProjectVerified=True), "retainedPartial", str(destination))
            file.write_bytes(b"tampered")
            with self.assertRaisesRegex(oracle.VerificationError, "manifest changed"): oracle.destination_manifest(root, manifest, "retainedPartial", str(destination))

    def test_checkpoint_interruption_requires_no_dispatch_no_orphan_adoption_and_competing_owner_refusal(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); records = save_records(root, ["prepared", "checkpointIntent", "outcomeUnknown", "reconciled"])
            save_records(root, ["prepared"], "competitor")
            archive = root / "orphan.zap21"; archive.write_bytes(b"authored-orphan")
            admission = {"mode": "checkpoint.afterArchive", "workerSha256": "a" * 64, "hostProcessId": 11}
            operation = {"id": "owned", "projectPath": str(root / "owned.ap21")}
            write(root / "interruption-marker.json", {"point": admission["mode"], "workerSha256": "a" * 64, "workerProcessId": 12, "processId": 13, "automaticReplay": False,
                "projectPath": operation["projectPath"], "output": str(archive)})
            write(root / "replay-refusal.json", {"operationId": "owned", "code": "RecoveryRequired", "nativeDispatchInvoked": False})
            refusal = {"code": "ResourceBusy", "operationId": "owned", "competitorId": "competitor", "hostProcessId": 21}
            write(root / "competing-owner-refusal.json", refusal)
            write(root / "matrix-reconciliation-result.json", {"completed": True, "operationId": "owned", "hostProcessId": 21, "automaticReplay": False})
            self.assertTrue(oracle.interruption_check(root, admission, records, operation)["hostRestartVerified"])
            self.assertFalse(oracle.interruption_check(root, {k: v for k, v in admission.items() if k != "hostProcessId"}, records, operation)["hostRestartVerified"])
            with self.assertRaisesRegex(oracle.VerificationError, "Host process was reused"): oracle.interruption_check(root, dict(admission, hostProcessId=21), records, operation)
            changed = copy.deepcopy(records); changed[-1]["checkpoints"] = [{"relativePath": "orphan.zap21", "sha256": "b" * 64}]
            with self.assertRaisesRegex(oracle.VerificationError, "orphan"): oracle.interruption_check(root, admission, changed, operation)
            write(root / "competing-owner-refusal.json", dict(refusal, code="Other"))
            with self.assertRaisesRegex(oracle.VerificationError, "Competing owner refusal"): oracle.interruption_check(root, admission, records, operation)

    def test_missing_review_or_startup_failure_cannot_become_matrix_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); write(root / "admission.json", {"mode": "checkpoint.afterArchive"}); write(root / "failure.json", {"error": "start timed out", "automaticReplay": False})
            result = oracle.verify(root)
            self.assertFalse(result["completed"]); self.assertEqual("reviewedExactDocumentDelta", result["issues"][0]["stage"])

    def test_all_eleven_boundary_markers_are_bound_and_retrieval_replay_is_refused(self):
        for point in sorted(oracle.NATIVE_POINTS | oracle.HOST_POINTS):
            with self.subTest(point=point), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); project = str(root / "owned.ap21"); destination = str(root / "retrieved")
                checkpoint = point.startswith("checkpoint.") or point == "host.watch_checkpoint"
                retrieval = point.startswith("retrieve.") or point == "host.watch_restore_native"
                states = ["prepared", "checkpointIntent" if checkpoint else "dispatchIntent", "outcomeUnknown"]
                if retrieval: states += ["recoveryIntent", "outcomeUnknown"]
                states += ["reconciled"]; records = save_records(root, states); save_records(root, ["prepared"], "competitor")
                if retrieval: records[-1]["recoveryDestinations"] = [{"resourceKey": "project:OWNED", "recoveryLocation": destination}]
                operation = {"id": "owned", "projectPath": project}; admission = {"mode": point, "workerSha256": "a" * 64, "hostProcessId": 11}
                archive = root / "unadopted.zap21"; archive.write_bytes(b"authored")
                marker = {"point": point, "automaticReplay": False}
                if point in oracle.NATIVE_POINTS:
                    marker.update(workerSha256="a" * 64, workerProcessId=12, processId=13,
                        projectPath=None if point == "retrieve.before" else str(Path(destination) / "owned.ap21") if point == "retrieve.after" else project,
                        output=destination if retrieval else str(archive) if point == "checkpoint.afterArchive" else None)
                else:
                    if checkpoint: response = {"checkpoint": ref_path(archive), "snapshot": {"project": {"path": project}, "observation": {"offline": True}}}
                    elif retrieval: response = {"destination": destination, "project": {"path": str(Path(destination) / "owned.ap21")}}
                    else:
                        response = snapshot(root, "apply", raw(fixture()), 13, project); response.update(saved=True, automaticReplay=False)
                    marker.update(hostProcessId=11, response=response)
                write(root / "interruption-marker.json", marker)
                write(root / "replay-refusal.json", {"operationId": "owned", "code": "RecoveryRequired", "nativeDispatchInvoked": False})
                write(root / "competing-owner-refusal.json", {"code": "ResourceBusy", "operationId": "owned", "competitorId": "competitor", "hostProcessId": 21})
                write(root / "matrix-reconciliation-result.json", {"completed": True, "operationId": "owned", "hostProcessId": 21, "automaticReplay": False})
                if retrieval: write(root / "retrieve-replay-refusal.json", {"code": "RecoveryRequired", "nativeRetrieveInvoked": False})
                result = oracle.interruption_check(root, admission, records, operation)
                self.assertTrue(result["hostRestartVerified"]); self.assertTrue(result["competingOwnerRefused"])
                if retrieval:
                    write(root / "retrieve-replay-refusal.json", {"code": "RecoveryRequired", "nativeRetrieveInvoked": True})
                    with self.assertRaisesRegex(oracle.VerificationError, "retrieval replay"): oracle.interruption_check(root, admission, records, operation)
                elif point in oracle.HOST_POINTS:
                    del marker["response"]; write(root / "interruption-marker.json", marker)
                    with self.assertRaisesRegex(oracle.VerificationError, "retained native response"): oracle.interruption_check(root, admission, records, operation)
                else:
                    marker["projectPath"] = "unrelated.ap21"; write(root / "interruption-marker.json", marker)
                    with self.assertRaisesRegex(oracle.VerificationError, "another project"): oracle.interruption_check(root, admission, records, operation)

    def test_no_checkpoint_recovery_verifies_fresh_unchanged_original_without_restoration_claim(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); baseline = raw(fixture()); changed = fixture(); comment(changed, "en-GB", "reviewed"); candidate = raw(changed)
            project = str(root / "original.ap21"); before = snapshot(root, "before", baseline, 1, project)
            original = snapshot(root, "original", baseline, 2, project)
            proof = {"original": original, "originalDisposition": "unchanged", "destinationDisposition": None, "destination": None,
                "automaticReplay": False, "nativeRestoreInvoked": False, "restoredProjectVerified": False}
            evidence = saved_json(root, "reconciled-proof.json", proof)
            records = save_records(root, ["prepared", "checkpointIntent", "outcomeUnknown", "reconciled"])
            records[-1]["reconciliation"] = [{"resourceKey": "project:OWNED", "disposition": "unchanged", "evidence": evidence}]
            write(root / "reconciled.json", {"operation": records[-1]})
            result = oracle.recovery_check(root, records, {"before": before}, {"projectPath": project}, baseline, candidate, True)
            self.assertEqual("unchanged", result["originalDisposition"]); self.assertFalse(result["restoredProjectVerified"])
            bad = copy.deepcopy(proof); bad["restoredProjectVerified"] = True
            records[-1]["reconciliation"][0]["evidence"] = saved_json(root, "bad-proof.json", bad); write(root / "reconciled.json", {"operation": records[-1]})
            with self.assertRaisesRegex(oracle.VerificationError, "cannot claim restoration"): oracle.recovery_check(root, records, {"before": before}, {"projectPath": project}, baseline, candidate, True)

    def test_no_dispatch_cannot_explain_candidate_appearing_in_original(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); baseline = raw(fixture()); changed = fixture(); comment(changed, "de-DE", "reviewed"); candidate = raw(changed)
            project = str(root / "original.ap21"); before = snapshot(root, "before", baseline, 1, project)
            original = snapshot(root, "original", candidate, 2, project)
            proof = {"original": original, "originalDisposition": "verifiedApplied", "destinationDisposition": None, "destination": None,
                "automaticReplay": False, "nativeRestoreInvoked": False, "restoredProjectVerified": False}
            evidence = saved_json(root, "proof.json", proof); records = save_records(root, ["prepared", "checkpointIntent", "outcomeUnknown", "reconciled"])
            records[-1]["reconciliation"] = [{"resourceKey": "project:OWNED", "disposition": "verifiedApplied", "evidence": evidence}]
            write(root / "reconciled.json", {"operation": records[-1]})
            with self.assertRaisesRegex(oracle.VerificationError, "undispatched edit"): oracle.recovery_check(root, records, {"before": before}, {"projectPath": project}, baseline, candidate, True)

    def test_lost_retrieval_requires_located_proof_and_distinct_fresh_restore_original(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); baseline = raw(fixture()); changed = fixture(); comment(changed, "nl-NL", "reviewed"); candidate = raw(changed)
            project = str(root / "original.ap21"); destination = str(root / "recovered"); recovered_path = str(root / "recovered/project/owned.ap21")
            before = snapshot(root, "before", baseline, 1, project)
            located = snapshot(root, "located-snapshot", baseline, 2, recovered_path)
            restored = snapshot(root, "restored-snapshot", baseline, 3, recovered_path)
            original = snapshot(root, "original-snapshot", candidate, 4, project)
            proof = {"recovered": {"project": {"path": recovered_path}, "processId": 2, "sessionEpoch": "native-2"}, "restored": located,
                "retrievalResponseRetained": False, "nativeRestoreInvoked": False, "automaticReplay": False}
            located_ref = saved_json(root, "located-proof.json", proof)
            original_ref = saved_json(root, "original-proof.json", original); restored_ref = saved_json(root, "restored-proof.json", restored)
            archive = root / "checkpoint.zap21"; archive.write_bytes(b"authored-checkpoint")
            records = save_records(root, ["prepared", "dispatchIntent", "outcomeUnknown", "recoveryIntent", "outcomeUnknown", "reconciled"])
            final = records[-1]; final["checkpoints"] = [{"relativePath": archive.name, "sha256": oracle.sha(archive.read_bytes())}]
            final["evidence"] = [{"gate": gate, "artifact": evidence} for gate, evidence in [("recovery.located", located_ref), ("recovery.original", original_ref), ("recovery.restored", restored_ref)]]
            final["recoveryDestinations"] = [{"resourceKey": "project:OWNED", "recoveryLocation": destination}]
            final["reconciliation"] = [{"resourceKey": "project:OWNED", "disposition": "verifiedApplied", "evidence": original_ref}]
            final["recoveryResolutions"] = [{"resourceKey": "project:OWNED", "recoveryLocation": destination, "disposition": "verifiedRestored", "evidence": restored_ref}]
            write(root / "reconciled.json", {"operation": final}); write(root / "located.json", {"located": True, "evidence": located_ref})
            write(root / "restored-verified.json", {"verified": "restored"})
            result = oracle.recovery_check(root, records, {"before": before}, {"projectPath": project}, baseline, candidate, True)
            self.assertTrue(result["restoredProjectVerified"]); self.assertFalse(result["retrievalResponseRetained"])
            proof["nativeRestoreInvoked"] = True; changed_ref = saved_json(root, "bad-located.json", proof)
            final["evidence"][0]["artifact"] = changed_ref; write(root / "reconciled.json", {"operation": final})
            with self.assertRaisesRegex(oracle.VerificationError, "repeated retrieval"): oracle.recovery_check(root, records, {"before": before}, {"projectPath": project}, baseline, candidate, True)

    def test_snapshot_readback_cannot_hide_native_id_renumbering_or_reuse_session(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); baseline = raw(fixture()); project = str(root / "owned.ap21")
            before = snapshot(root, "before", baseline, 1, project); after = snapshot(root, "after", baseline, 2, project)
            oracle.snapshot_check(root, after, baseline, before, True, project)
            with self.assertRaisesRegex(oracle.VerificationError, "Fresh saved"): oracle.snapshot_check(root, before, baseline, before, True, project)
            changed = fixture(); oracle.row_elements(changed)[0].attrib["ID"] = "AA"
            bad = snapshot(root, "bad", raw(changed), 3, project)
            with self.assertRaisesRegex(oracle.VerificationError, "including native IDs"): oracle.snapshot_check(root, bad, baseline, before, True, project)

    def test_malformed_native_ids_dtd_duplicate_attributes_and_force_mutation_refused(self):
        old = fixture()
        for value in ("", "FFFFFFFFF", "-1", "0"):
            changed = copy.deepcopy(old); oracle.row_elements(changed)[0].attrib["ID"] = value
            with self.assertRaises(oracle.VerificationError): oracle.document(raw(changed))
        changed = copy.deepcopy(old); del oracle.row_elements(changed)[0].attrib["ID"]
        with self.assertRaises(oracle.VerificationError): oracle.document(raw(changed))
        with self.assertRaises(oracle.VerificationError): oracle.document(FIXTURE.parent.joinpath("ForceTable.xml").read_bytes())
        with self.assertRaises(oracle.VerificationError): oracle.document(b'<!DOCTYPE Document><Document/>')
        changed = copy.deepcopy(old); attrs = oracle.row_elements(changed)[0].find("AttributeList"); attrs.append(copy.deepcopy(attrs.find("ModifyValue")))
        with self.assertRaises(oracle.VerificationError): oracle.document(raw(changed))


class ActualHostLifecycleTests(unittest.TestCase):
    """Authored process receipts test the oracle, never claim real process execution."""
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def make(self, mode="checkpoint.beforeArchive"):
        host = self.root / "TiaMcp.Host.exe"; host.write_bytes(b"authored Host bytes")
        worker = self.root / "TiaMcp.Openness.V21.exe"; worker.write_bytes(b"authored worker bytes")
        admission = {"actualMcpHost": True, "mode": mode, "host": str(host), "hostSha256": oracle.sha(host.read_bytes()),
                     "worker": str(worker), "workerSha256": oracle.sha(worker.read_bytes()), "hostProcessId": 110}
        interrupted = mode in oracle.NATIVE_POINTS | oracle.HOST_POINTS
        for index in range(1, (2 if interrupted else 1) + 1):
            pid, created = 100 + index * 10, 1000.0 + index * 100
            identity = {"pid": pid, "parentPid": 99, "created": created, "name": host.name, "exe": str(host)}
            worker_identity = {"pid": pid + 1, "parentPid": pid, "created": created + 1, "name": worker.name, "exe": str(worker)}
            native_identity = {"pid": pid + 2, "parentPid": pid + 1, "created": created + 2, "name": "Siemens.Automation.Portal.exe", "exe": str(self.root / "Siemens.Automation.Portal.exe")}
            fault = interrupted and index == 1
            host_fault, worker_fault = fault and mode in oracle.HOST_POINTS, fault and mode in oracle.NATIVE_POINTS
            worker_code = 73 if worker_fault else 0
            exits = [{"identity": info, "proof": {"processId": info["pid"], "completed": True, "handleCaptured": True,
                       "processExitVerified": True, "handleClosed": True, "exitCode": worker_code if info is worker_identity else 0}}
                     for info in (worker_identity, native_identity)]
            close = {"hostExitCode": 74 if host_fault else 1 if worker_fault else 0, "forcedHostStop": False,
                     "stderrEofObserved": True, "workerExits": [] if host_fault else [{"processId": worker_identity["pid"],
                     "startedAtUtc": datetime.fromtimestamp(worker_identity["created"], timezone.utc).isoformat(),
                     "processExitVerified": True, "forcedTermination": False, "exitCode": worker_code}]}
            write(self.root / f"host-{index:02}-start.json", {"identity": identity, "point": mode if fault else "none", "processes": []})
            write(self.root / f"host-{index:02}-processes.json", [worker_identity, native_identity])
            write(self.root / f"host-{index:02}-close.json", {"identity": identity, "closedAt": created + 50,
                  "close": close, "exits": exits, "logs": [], "expectedLoss": fault})
        if interrupted:
            write(self.root / "interruption-marker.json", {"workerProcessId": 111, "processId": 112, "hostProcessId": 110,
                  "hostStartedAtUtc": datetime.fromtimestamp(1100, timezone.utc).isoformat()})
            write(self.root / "matrix-reconciliation-result.json", {"hostProcessId": 120})
            write(self.root / "competing-owner-refusal.json", {"hostProcessId": 120})
        write(self.root / "review.json", {"review": {"before": {"processId": 112}}})
        return admission, interrupted

    def mutate(self, name, change):
        path = self.root / name
        value = oracle.load(path); change(value); write(path, value)

    def cleanup(self, admission):
        helper = self.root / "OwnedPortalCleanup.exe"; helper.write_bytes(b"authored bounded cleanup binary")
        source = self.root / "CleanupProgram.cs"; source.write_bytes(b"authored cleanup source")
        admission["nativeCleanupHelper"] = {"path": str(helper), "sha256": oracle.sha(helper.read_bytes()),
            "sources": [{"path": str(source), "sha256": oracle.sha(source.read_bytes())}]}
        marker_path = self.root / "interruption-marker.json"
        if not marker_path.exists(): write(marker_path, {"workerProcessId": 111, "processId": 112})
        self.mutate(marker_path.name, lambda x: x.update(workerSha256=admission["workerSha256"], projectPath=None))
        closed = oracle.load(self.root / "host-01-close.json")
        intent = {"identity": copy.deepcopy(closed["exits"][1]["identity"]), "markerSha256": oracle.sha(marker_path.read_bytes()),
            "helperSha256": admission["nativeCleanupHelper"]["sha256"], "projectPath": None,
            "method": "TiaPortalProcess.Dispose", "explicitNativeTermination": True, "automaticReplay": False,
            "requestedAtUtc": datetime.fromtimestamp(1110, timezone.utc).isoformat(), "sameOsHandleRetained": True}
        result = dict(intent, completed=True, exitCode=0, closedAtUtc=datetime.fromtimestamp(1111, timezone.utc).isoformat())
        write(self.root / "coordinator-native-close-intent.json", intent)
        write(self.root / "coordinator-native-close.json", result)
        closed["closeRequestedAt"] = 1120
        closed["exits"][1]["proof"]["capturedAtUtc"] = datetime.fromtimestamp(1102.2, timezone.utc).isoformat()
        write(self.root / "host-01-close.json", closed)
        return intent, result

    def test_all_actual_interruption_lifetimes_and_normal_profile(self):
        for mode in sorted(oracle.NATIVE_POINTS | oracle.HOST_POINTS | {"comment-de"}):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                previous, self.root = self.root, Path(temp)
                try:
                    admission, interrupted = self.make(mode)
                    result = oracle.actual_host_lifecycle(self.root, admission, interrupted)
                    self.assertEqual(2 if interrupted else 1, result["hostCount"])
                    self.assertEqual(interrupted, result["closedBeforeRestart"])
                finally:
                    self.root = previous

    def test_missing_start_close_or_process_inventory_is_not_pid_proof(self):
        admission, interrupted = self.make()
        for name in ("host-01-start.json", "host-01-close.json", "host-02-start.json", "host-02-close.json", "host-01-processes.json"):
            path = self.root / name; data = path.read_bytes(); path.unlink()
            with self.subTest(name=name), self.assertRaises((oracle.VerificationError, OSError)):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)
            path.write_bytes(data)

    def test_empty_monitor_and_worker_exit_receipts_refused(self):
        admission, interrupted = self.make()
        self.mutate("host-01-close.json", lambda x: x.update(exits=[]))
        with self.assertRaisesRegex(oracle.VerificationError, "inventory is empty"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)
        admission, interrupted = self.make()
        self.mutate("host-02-close.json", lambda x: x["close"].update(workerExits=[]))
        with self.assertRaisesRegex(oracle.VerificationError, "worker shutdown receipt"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_forced_host_wrong_exit_and_missing_eof_refused(self):
        for patch in ({"forcedHostStop": True}, {"hostExitCode": 0}, {"stderrEofObserved": False}):
            admission, interrupted = self.make()
            self.mutate("host-01-close.json", lambda x: x["close"].update(patch))
            with self.subTest(patch=patch), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_host_fault_requires_74_and_exact_marker_start_time(self):
        admission, interrupted = self.make("host.watch_checkpoint")
        self.mutate("host-01-close.json", lambda x: x["close"].update(hostExitCode=1))
        with self.assertRaisesRegex(oracle.VerificationError, "admitted boundary"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)
        admission, interrupted = self.make("host.watch_checkpoint")
        self.mutate("interruption-marker.json", lambda x: x.update(hostStartedAtUtc="1970-01-01T00:00:00+00:00"))
        with self.assertRaisesRegex(oracle.VerificationError, "exact process lifetime"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_nonzero_host_fault_worker_and_wrong_worker_fault_code_refused(self):
        for mode, code in (("host.watch_checkpoint", 73), ("checkpoint.beforeArchive", 0)):
            admission, interrupted = self.make(mode)
            self.mutate("host-01-close.json", lambda x: x["exits"][0]["proof"].update(exitCode=code))
            with self.subTest(mode=mode), self.assertRaisesRegex(oracle.VerificationError, "Worker exit differs"):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_closed_before_restart_and_close_identity_required(self):
        for patch in ({"closedAt": 1201}, {"closedAt": None}, {"identity": {"pid": 110}}):
            admission, interrupted = self.make()
            self.mutate("host-01-close.json", lambda x: x.update(patch))
            with self.subTest(patch=patch), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_incomplete_or_mismatched_exact_handle_proofs_refused(self):
        for patch in ({"completed": False}, {"handleCaptured": False}, {"processExitVerified": False}, {"handleClosed": False}, {"processId": 999}, {"exitCode": None}):
            admission, interrupted = self.make()
            self.mutate("host-01-close.json", lambda x: x["exits"][1]["proof"].update(patch))
            with self.subTest(patch=patch), self.assertRaisesRegex(oracle.VerificationError, "handle exit proof"):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_worker_shutdown_receipt_pid_creation_time_code_and_forcing_bound(self):
        for patch in ({"processId": 999}, {"startedAtUtc": "1970-01-01T00:00:00Z"}, {"exitCode": 1}, {"forcedTermination": True}):
            admission, interrupted = self.make()
            self.mutate("host-02-close.json", lambda x: x["close"]["workerExits"][0].update(patch))
            with self.subTest(patch=patch), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_binary_mutation_and_unrelated_native_or_worker_identity_refused(self):
        admission, interrupted = self.make()
        Path(admission["worker"]).write_bytes(b"changed admitted worker")
        with self.assertRaisesRegex(oracle.VerificationError, "binary bytes changed"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)
        for index, patch in ((0, {"parentPid": 99}), (1, {"parentPid": 99}), (0, {"exe": "other-worker.exe"})):
            admission, interrupted = self.make()
            self.mutate("host-01-close.json", lambda x: x["exits"][index]["identity"].update(patch))
            self.mutate("host-01-processes.json", lambda x: x[index].update(patch))
            with self.subTest(patch=patch), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_reused_process_identity_or_untracked_reviewed_native_refused(self):
        admission, interrupted = self.make()
        old = oracle.load(self.root / "host-01-close.json")["exits"][1]
        self.mutate("host-02-close.json", lambda x: x["exits"].__setitem__(1, old))
        self.mutate("host-02-processes.json", lambda x: x.__setitem__(1, old["identity"]))
        with self.assertRaisesRegex(oracle.VerificationError, "identity was reused"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)
        admission, interrupted = self.make()
        self.mutate("review.json", lambda x: x["review"]["before"].update(processId=999))
        with self.assertRaisesRegex(oracle.VerificationError, "baseline lacks exact cleanup"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_marker_and_reconciliation_must_match_the_retained_hosts(self):
        for name, patch in (("interruption-marker.json", {"workerProcessId": 999}),
                            ("matrix-reconciliation-result.json", {"hostProcessId": 999}),
                            ("competing-owner-refusal.json", {"hostProcessId": 999})):
            admission, interrupted = self.make()
            self.mutate(name, lambda x: x.update(patch))
            with self.subTest(name=name), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_worker_fault_explicit_cleanup_is_intervention_not_normal_lifecycle(self):
        admission, interrupted = self.make("retrieve.before")
        self.cleanup(admission)
        result = oracle.actual_host_lifecycle(self.root, admission, interrupted)
        self.assertTrue(result["coordinatorInterventionRequired"] and result["explicitNativeTermination"])
        self.assertFalse(result["normalLifecyclePassed"])
        self.assertNotIn("forcedTermination", result)
        self.assertTrue(result["coordinatorNativeCleanup"]["originalMonitorHandleExitVerified"])

    def test_explicit_cleanup_never_relaxes_normal_or_host_fault_lifecycle(self):
        for mode in ("comment-de", "host.watch_checkpoint"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                previous, self.root = self.root, Path(temp)
                try:
                    admission, interrupted = self.make(mode); self.cleanup(admission)
                    with self.assertRaisesRegex(oracle.VerificationError, "only for an admitted worker interruption"):
                        oracle.actual_host_lifecycle(self.root, admission, interrupted)
                finally: self.root = previous

    def test_explicit_cleanup_does_not_accept_uq05_style_incomplete_host_receipt(self):
        for patch in ({"processExitVerified": False, "exitCode": None}, {"exitCode": None}):
            admission, interrupted = self.make("retrieve.before"); self.cleanup(admission)
            self.mutate("host-01-close.json", lambda x: x["close"]["workerExits"][0].update(patch))
            with self.subTest(patch=patch), self.assertRaisesRegex(oracle.VerificationError, "Worker shutdown receipt"):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)
        admission, interrupted = self.make("retrieve.before"); self.cleanup(admission)
        self.mutate("host-01-close.json", lambda x: x["close"].update(stderrEofObserved=False))
        with self.assertRaisesRegex(oracle.VerificationError, "stderr EOF"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_cleanup_requires_admitted_helper_sources_and_unchanged_bytes(self):
        for change in ("absent", "emptySources", "changedBinary", "changedSource", "duplicateSource"):
            admission, interrupted = self.make(); self.cleanup(admission)
            if change == "absent": del admission["nativeCleanupHelper"]
            elif change == "emptySources": admission["nativeCleanupHelper"]["sources"] = []
            elif change == "changedBinary": Path(admission["nativeCleanupHelper"]["path"]).write_bytes(b"different binary")
            elif change == "changedSource": Path(admission["nativeCleanupHelper"]["sources"][0]["path"]).write_bytes(b"different source")
            else: admission["nativeCleanupHelper"]["sources"] *= 2
            with self.subTest(change=change), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_cleanup_intent_and_return_are_required_and_immutable(self):
        admission, interrupted = self.make(); self.cleanup(admission)
        for name in ("coordinator-native-close-intent.json", "coordinator-native-close.json"):
            path = self.root / name; data = path.read_bytes(); path.unlink()
            with self.subTest(name=name), self.assertRaisesRegex(oracle.VerificationError, "intent and return"):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)
            path.write_bytes(data)
        self.mutate("coordinator-native-close.json", lambda x: x.update(method="Other"))
        with self.assertRaisesRegex(oracle.VerificationError, "changed the immutable intent"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_cleanup_unknown_method_replay_and_missing_explicit_termination_refused(self):
        for patch in ({"method": "TerminateProcess"}, {"automaticReplay": True}, {"explicitNativeTermination": False}, {"sameOsHandleRetained": False}):
            admission, interrupted = self.make(); self.cleanup(admission)
            for name in ("coordinator-native-close-intent.json", "coordinator-native-close.json"):
                self.mutate(name, lambda x: x.update(patch))
            with self.subTest(patch=patch), self.assertRaisesRegex(oracle.VerificationError, "exact explicit termination"):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def os_cleanup(self, admission):
        self.cleanup(admission)
        admission["nativeCleanupHelper"]["method"] = "TerminateProcess(exactOwnedHandle)"
        for name in ("coordinator-native-close-intent.json", "coordinator-native-close.json"):
            self.mutate(name, lambda x: x.update(method="TerminateProcess(exactOwnedHandle)", projectIdentityBasis="Durable interruption marker from the exact admitted worker, not a new native attachment"))
        self.mutate("coordinator-native-close.json", lambda x: x.update(exitCode=75))
        self.mutate("host-01-close.json", lambda x: x["exits"][1]["proof"].update(exitCode=75))

    def test_os_orphan_cleanup_requires_explicit_method_and_marker_basis(self):
        admission, interrupted = self.make("retrieve.before"); self.os_cleanup(admission)
        result = oracle.actual_host_lifecycle(self.root, admission, interrupted)
        self.assertTrue(result["coordinatorInterventionRequired"])
        self.assertFalse(result["normalLifecyclePassed"])
        del admission["nativeCleanupHelper"]["method"]
        with self.assertRaisesRegex(oracle.VerificationError, "explicitly admitted"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_os_orphan_cleanup_cannot_claim_live_project_observation(self):
        admission, interrupted = self.make("retrieve.before"); self.os_cleanup(admission)
        for name in ("coordinator-native-close-intent.json", "coordinator-native-close.json"):
            self.mutate(name, lambda x: x.update(projectIdentityBasis="new native attachment"))
        with self.assertRaisesRegex(oracle.VerificationError, "explicitly admitted"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_os_orphan_cleanup_requires_its_exact_terminal_code(self):
        admission, interrupted = self.make("retrieve.before"); self.os_cleanup(admission)
        self.mutate("coordinator-native-close.json", lambda x: x.update(exitCode=0))
        self.mutate("host-01-close.json", lambda x: x["exits"][1]["proof"].update(exitCode=0))
        with self.assertRaisesRegex(oracle.VerificationError, "terminal code differs"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_cleanup_helper_identity_marker_hash_and_project_are_bound(self):
        for patch in ({"markerSha256": "0" * 64}, {"helperSha256": "0" * 64}, {"identity": {"pid": 112}}, {"projectPath": "another.ap21"}):
            admission, interrupted = self.make(); self.cleanup(admission)
            for name in ("coordinator-native-close-intent.json", "coordinator-native-close.json"):
                self.mutate(name, lambda x: x.update(patch))
            with self.subTest(patch=patch), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_cleanup_original_native_monitor_must_prove_same_exit(self):
        admission, interrupted = self.make(); self.cleanup(admission)
        self.mutate("coordinator-native-close.json", lambda x: x.update(exitCode=1))
        with self.assertRaisesRegex(oracle.VerificationError, "same terminal exit"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)
        admission, interrupted = self.make(); self.cleanup(admission)
        self.mutate("host-01-close.json", lambda x: x["exits"][1]["proof"].update(completed=False, processExitVerified=False))
        with self.assertRaisesRegex(oracle.VerificationError, "handle exit proof"):
            oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_cleanup_capture_request_return_host_close_and_restart_order_required(self):
        for change in ("lateCapture", "missingCapture", "missingCloseRequest", "lateReturn", "closeBeforeReturn", "naiveTime"):
            admission, interrupted = self.make(); self.cleanup(admission)
            if change == "lateCapture":
                self.mutate("host-01-close.json", lambda x: x["exits"][1]["proof"].update(capturedAtUtc=datetime.fromtimestamp(1110.5, timezone.utc).isoformat()))
            elif change == "missingCapture": self.mutate("host-01-close.json", lambda x: x["exits"][1]["proof"].pop("capturedAtUtc"))
            elif change == "missingCloseRequest": self.mutate("host-01-close.json", lambda x: x.pop("closeRequestedAt"))
            elif change == "lateReturn": self.mutate("coordinator-native-close.json", lambda x: x.update(closedAtUtc=datetime.fromtimestamp(1250, timezone.utc).isoformat()))
            elif change == "closeBeforeReturn": self.mutate("host-01-close.json", lambda x: x.update(closeRequestedAt=1109))
            else: self.mutate("coordinator-native-close.json", lambda x: x.update(closedAtUtc="1970-01-01T00:18:31"))
            with self.subTest(change=change), self.assertRaises(oracle.VerificationError):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)

    def test_cleanup_incomplete_helper_exit_cannot_be_replaced_by_native_monitor(self):
        for patch in ({"completed": False}, {"exitCode": None}, {"exitCode": True}):
            admission, interrupted = self.make(); self.cleanup(admission)
            self.mutate("coordinator-native-close.json", lambda x: x.update(patch))
            with self.subTest(patch=patch), self.assertRaisesRegex(oracle.VerificationError, "helper did not prove terminal"):
                oracle.actual_host_lifecycle(self.root, admission, interrupted)


def ref_path(path): return {"path": str(path), "sha256": oracle.sha(path.read_bytes()), "bytes": path.stat().st_size}


if __name__ == "__main__": unittest.main()
