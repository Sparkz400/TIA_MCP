"""Portable adversarial tests of the independent watch lifecycle evidence oracle."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

import verify_watch_workflow as oracle


def fixture(literal="FALSE"):
    root = ET.Element("Document"); ET.SubElement(root, "Engineering", {"version": "V21"})
    info = ET.SubElement(root, "DocumentInfo"); ET.SubElement(info, "Created").text = "2026-10-15T00:00:00Z"; ET.SubElement(info, "ExportSetting").text = "WithDefaults"
    table = ET.SubElement(root, oracle.PREFIX + "PlcWatchTable", {"ID": "0"})
    ET.SubElement(ET.SubElement(table, "AttributeList"), "Name").text = "MilestoneWatch"
    row = ET.SubElement(ET.SubElement(table, "ObjectList"), oracle.PREFIX + "PlcWatchTableEntry", {"ID": "1", "CompositionName": "Entries"})
    attrs = ET.SubElement(row, "AttributeList")
    for name, value in [("Name", oracle.SYMBOL), ("DisplayFormat", "Bool"), ("ModifyValue", literal), ("Opaque", "  ")]: ET.SubElement(attrs, name).text = value
    text = ET.SubElement(ET.SubElement(row, "ObjectList"), "MultilingualText", {"ID": "2", "CompositionName": "Comment"})
    items = ET.SubElement(text, "ObjectList")
    for index, (culture, value) in enumerate(oracle.COMMENTS.items(), 3):
        attrs = ET.SubElement(ET.SubElement(items, "MultilingualTextItem", {"ID": str(index), "CompositionName": "Items"}), "AttributeList")
        ET.SubElement(attrs, "Culture").text = culture; ET.SubElement(attrs, "Text").text = value
    return root


def raw(root): return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def typed(literal="TRUE"):
    return {"target": {"name": "MilestoneWatch", "kind": "watch", "isConsistent": True, "rows": [{"kind": "PlcWatchTableEntry", "name": oracle.SYMBOL,
        "displayFormat": "Bool", "modifyValue": literal, "modifyIntention": False}]}, "observation": {"offline": True}}


def write(path, value): path.write_text(json.dumps(value), encoding="utf-8")


class WatchWorkflowOracleTests(unittest.TestCase):
    def test_authored_three_language_xml_and_single_literal_change(self):
        oracle.target_oracle(raw(fixture()), "FALSE")
        oracle.only_literal_change(raw(fixture()), raw(fixture("TRUE")))

    def test_comment_language_removal_is_not_preserved(self):
        root = fixture(); items = root.find(".//MultilingualText/ObjectList"); items.remove(items[-1])
        with self.assertRaisesRegex(oracle.VerificationError, "three authored comments"): oracle.target_oracle(raw(root), "FALSE")

    def test_duplicate_comment_culture_is_rejected(self):
        root = fixture(); root.findall(".//Culture")[1].text = "de-DE"
        with self.assertRaisesRegex(oracle.VerificationError, "Duplicate comment"): oracle.target_oracle(raw(root), "FALSE")

    def test_extra_target_row_is_not_normalized_away(self):
        root = fixture(); objects = oracle.table(root).find("ObjectList"); ET.SubElement(objects, oracle.PREFIX + "PlcTableCommentEntry", {"ID": "A", "CompositionName": "Entries"})
        with self.assertRaisesRegex(oracle.VerificationError, "one authored watch row"): oracle.target_oracle(raw(root), "FALSE")

    def test_unknown_leaf_whitespace_change_invalidates_approved_diff(self):
        changed = fixture("TRUE"); changed.find(".//Opaque").text = " "
        with self.assertRaisesRegex(oracle.VerificationError, "beyond the one approved"): oracle.only_literal_change(raw(fixture()), raw(changed))

    def test_unknown_nested_whitespace_survives_semantic_comparison(self):
        before = fixture(); opaque = before.find(".//Opaque"); opaque.text = None; ET.SubElement(opaque, "Child").tail = "  "
        after = copy.deepcopy(before); after.find(".//Opaque/Child").tail = " "
        self.assertNotEqual(oracle.xml_key(before), oracle.xml_key(after))

    def test_opaque_native_named_containers_are_not_normalized(self):
        before = fixture(); opaque = before.find(".//Opaque")
        ET.SubElement(ET.SubElement(opaque, "AttributeList"), "Text").tail = "  "
        after = copy.deepcopy(before); after.find(".//Opaque/AttributeList/Text").tail = " "
        self.assertNotEqual(oracle.xml_key(before), oracle.xml_key(after))
        with self.assertRaisesRegex(oracle.VerificationError, "beyond the one approved"):
            after.find(".//ModifyValue").text = "TRUE"
            oracle.only_literal_change(raw(before), raw(after))

    def test_opaque_created_field_is_not_export_time(self):
        before = fixture(); ET.SubElement(ET.SubElement(before.find(".//Opaque"), "DocumentInfo"), "Created").text = "retained"
        after = copy.deepcopy(before); after.find(".//Opaque/DocumentInfo/Created").text = "changed"
        self.assertNotEqual(oracle.xml_key(before), oracle.xml_key(after))

    def test_outside_root_annotations_must_match_instead_of_disappearing(self):
        for annotation in (b"<!-- retained -->", b"<?vendor retained?>"):
            for before in (True, False):
                with self.subTest(annotation=annotation, before=before):
                    data = raw(fixture())
                    data = data.replace(b"<Document>", annotation + b"<Document>") if before else data + annotation
                    with self.assertRaisesRegex(oracle.VerificationError, "beyond the one approved"):
                        oracle.only_literal_change(data, raw(fixture("TRUE")))
                    candidate = raw(fixture("TRUE"))
                    candidate = candidate.replace(b"<Document>", annotation + b"<Document>") if before else candidate + annotation
                    oracle.only_literal_change(data, candidate)

    def test_inside_root_annotations_remain_significant(self):
        before = fixture(); before.append(ET.Comment("retained")); before.append(ET.ProcessingInstruction("vendor", "retained"))
        after = copy.deepcopy(before); after[-1].text = "vendor changed"
        self.assertNotEqual(oracle.xml_key(oracle.xml(raw(before))), oracle.xml_key(oracle.xml(raw(after))))

    def test_only_export_time_and_known_indentation_are_volatile(self):
        before = fixture(); after = copy.deepcopy(before); after.find("DocumentInfo/Created").text = "different timestamp"
        after.text = "\n  "; after.find("Engineering").tail = "\n  "
        self.assertEqual(oracle.xml_key(before), oracle.xml_key(after))
        self.assertNotEqual(oracle.xml_key(before, False), oracle.xml_key(after, False))

    def test_blank_comment_peer_loss_changes_xml(self):
        before = fixture(); table = oracle.table(before); table.find("AttributeList/Name").text = "UnchangedPeer"
        objects = table.find("ObjectList"); objects.clear(); ET.SubElement(objects, oracle.PREFIX + "PlcTableCommentEntry", {"ID": "1", "CompositionName": "Entries"})
        after = copy.deepcopy(before); oracle.table(after).find("ObjectList").clear()
        self.assertNotEqual(oracle.xml_key(before), oracle.xml_key(after))

    def test_xml_space_preserve_is_respected(self):
        before = fixture(); objects = oracle.table(before).find("ObjectList"); objects.attrib["{http://www.w3.org/XML/1998/namespace}space"] = "preserve"; objects.text = " "
        after = copy.deepcopy(before); oracle.table(after).find("ObjectList").text = "  "
        self.assertNotEqual(oracle.xml_key(before), oracle.xml_key(after))

    def test_typed_native_value_and_intention_are_independent(self):
        oracle.typed_oracle(typed(), "TRUE")
        for key, value in [("modifyValue", "FALSE"), ("name", '"Other_DB".Active'), ("displayFormat", "HEX"), ("modifyIntention", True)]:
            changed = typed(); changed["target"]["rows"][0][key] = value
            with self.assertRaises(oracle.VerificationError): oracle.typed_oracle(changed, "TRUE")

    def test_complete_peer_json_change_rejected_even_with_equal_digest(self):
        before = {"peers": {"watch:root/UnchangedPeer": "first", "hardware": []}, "peerSha256": "same"}
        after = copy.deepcopy(before); after["peers"]["watch:root/UnchangedPeer"] = "changed"
        with self.assertRaisesRegex(oracle.VerificationError, "Complete peer JSON changed"): oracle.peer_check(Path("."), before, after)

    def test_peer_xml_annotations_cannot_hide_behind_equal_reported_digests(self):
        for table_peer in (True, False):
            with self.subTest(table_peer=table_peer), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); snapshots = []
                for name in ("before", "after"):
                    folder = root/name; folder.mkdir(); path = folder/("peer.xml" if table_peer else "block-0.xml")
                    path.write_bytes(raw(fixture()) + b"<!-- " + name.encode() + b" -->")
                    snapshots.append({"peers": {}, "peerSha256": "unchanged claim", "target": {"artifact": {"path": str(folder/'target.xml')}},
                        "tables": [{"kind": "watch", "group": "root", "name": "Peer", "artifact": oracle.ref(path)}] if table_peer else []})
                with self.assertRaisesRegex(oracle.VerificationError, "Independent complete peer XML changed"):
                    oracle.peer_check(root, *snapshots)

    def test_target_readback_includes_outer_annotations(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); path = root/'target.xml'; candidate = raw(fixture("TRUE")) + b"<?vendor retained?>"
            path.write_bytes(candidate); snapshot = typed(); snapshot['target']['artifact'] = oracle.ref(path); snapshot['tables'] = []
            receipt = root/'receipt.json'; write(receipt, snapshot); snapshot['receipt'] = oracle.ref(receipt)
            oracle.snapshot_check(root, snapshot, "TRUE", candidate)
            path.write_bytes(candidate.replace(b'vendor retained', b'vendor changed')); snapshot['target']['artifact'] = oracle.ref(path)
            write(receipt, {k: v for k, v in snapshot.items() if k != 'receipt'}); snapshot['receipt'] = oracle.ref(receipt)
            with self.assertRaisesRegex(oracle.VerificationError, "complete reviewed document"):
                oracle.snapshot_check(root, snapshot, "TRUE", candidate)

    def test_opaque_unknown_metadata_and_comment_nodes_are_not_discarded(self):
        before = fixture(); before.append(ET.Comment("first")); after = copy.deepcopy(before); after[-1].text = "changed"
        self.assertNotEqual(oracle.xml_key(before), oracle.xml_key(after))

    def test_artifact_tamper_and_root_escape_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); file = root / "input.xml"; file.write_bytes(raw(fixture()))
            reference = {"relativePath": "input.xml", "sha256": oracle.sha(file.read_bytes())}; oracle.artifact(root, reference)
            file.write_bytes(raw(fixture("TRUE")))
            with self.assertRaises(oracle.VerificationError): oracle.artifact(root, reference)
            with self.assertRaisesRegex(oracle.VerificationError, "escapes"): oracle.artifact(root, {"relativePath": "../input.xml", "sha256": "0" * 64})

    def test_journal_chain_tamper_and_duplicate_dispatch_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); directory = root / "engineering-operations" / "authored"; directory.mkdir(parents=True)
            plan = {"operation": "authored", "inputs": []}; records = []
            for index, state in enumerate(["prepared", "dispatchIntent", "outcomeUnknown"]):
                records.append({"id": "authored", "revision": index, "state": state, "plan": plan, "planSha256": oracle.canonical_sha(plan),
                    "previousSha256": oracle.canonical_sha(records[-1]) if records else None, "checkpoints": [], "evidence": []})
                write(directory / f"{index:06}.json", records[-1])
            oracle.journal_check(root, "authored")
            changed = copy.deepcopy(records[0]); changed["state"] = "succeeded"; write(directory / "000000.json", changed)
            with self.assertRaisesRegex(oracle.VerificationError, "hash chain"): oracle.journal_check(root, "authored")
            write(directory / "000000.json", records[0]); changed = copy.deepcopy(records[-1]); changed["state"] = "dispatchIntent"; write(directory / "000002.json", changed)
            with self.assertRaisesRegex(oracle.VerificationError, "replay detected"): oracle.journal_check(root, "authored")

    def test_returned_readback_is_an_evidence_revision_not_another_dispatch(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); directory = root / "engineering-operations" / "authored"; directory.mkdir(parents=True)
            (root / "readback.json").write_bytes(b'{"nativeResult":"fixture"}')
            proof = {"gate": "dispatch.readback", "artifact": {"relativePath": "readback.json", "sha256": oracle.sha((root / "readback.json").read_bytes())}}
            plan = {"operation": "authored", "inputs": []}; records = []
            for index, (state, evidence) in enumerate([("prepared", []), ("dispatchIntent", []), ("dispatchIntent", [proof])]):
                records.append({"id": "authored", "revision": index, "state": state, "plan": plan, "planSha256": oracle.canonical_sha(plan),
                    "previousSha256": oracle.canonical_sha(records[-1]) if records else None, "checkpoints": [], "evidence": evidence})
                write(directory / f"{index:06}.json", records[-1])
            self.assertEqual(3, len(oracle.journal_check(root, "authored")))
            changed = copy.deepcopy(records[-1]); changed["evidence"] = []
            write(directory / "000002.json", changed)
            with self.assertRaisesRegex(oracle.VerificationError, "replay detected"): oracle.journal_check(root, "authored")
            write(directory / "000002.json", records[-1]); (root / "readback.json").write_bytes(b'changed')
            with self.assertRaises(oracle.VerificationError): oracle.journal_check(root, "authored")

    def test_json_duplicate_properties_and_dtd_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            file = Path(temp) / "data.json"; file.write_text('{"state":"succeeded","state":"failed"}')
            with self.assertRaisesRegex(oracle.VerificationError, "Duplicate JSON"): oracle.load(file)
        with self.assertRaisesRegex(oracle.VerificationError, "DTD"): oracle.xml(b'<!DOCTYPE Document [<!ENTITY x "x">]><Document/>')

    def test_dotnet_canonical_escaping_is_explicit(self):
        self.assertEqual('{"a":"x\\u002By\\u0022\\u00F6","z":[true,null,1]}', oracle.dotnet_canonical({"z": [True, None, 1], "a": 'x+y"ö'}))

    def test_strict_byte_oracle_masks_only_native_created(self):
        before = raw(fixture()); after = before.replace(b"2026-10-15T00:00:00Z", b"2026-10-16T02:03:04Z")
        self.assertEqual(oracle.without_created_bytes(before), oracle.without_created_bytes(after))
        for changed in [after.replace(b"<Opaque>  </Opaque>", b"<Opaque> </Opaque>"), after.replace(b"<Document>", b"<Document>\n  "), after.replace(b"WithDefaults", b"None")]:
            self.assertNotEqual(oracle.without_created_bytes(before), oracle.without_created_bytes(changed))

    def test_strict_byte_oracle_rejects_fake_or_ambiguous_timestamp(self):
        for change in [b"not-a-date", b"<Created>2026-10-15T00:00:00Z</Created>"]:
            with self.assertRaises((oracle.VerificationError, ValueError)):
                oracle.without_created_bytes(raw(fixture()).replace(b"2026-10-15T00:00:00Z", change))

    def test_strict_native_exports_allow_one_literal_but_no_indentation_loss(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            def snapshot(folder, data):
                folder.mkdir(); file = folder / "watch-0.xml"; file.write_bytes(data)
                entry = {"artifact": oracle.ref(file)}
                return {"target": entry, "tables": []}
            before = snapshot(root / "before", raw(fixture())); after = snapshot(root / "after", raw(fixture("TRUE")))
            result = oracle.strict_native_exports(root, before, after, "TRUE"); self.assertFalse(result["whitespaceNormalized"])
            changed = snapshot(root / "changed", raw(fixture("TRUE")).replace(b"<Document>", b"<Document>\n  "))
            with self.assertRaisesRegex(oracle.VerificationError, "Native target bytes"):
                oracle.strict_native_exports(root, before, changed, "TRUE")

    def test_explicit_replay_refusal_is_bound_and_requires_no_dispatch(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); file = root / "replay-refusal.json"
            good = {"operationId": "authored", "code": "RecoveryRequired", "nativeDispatchInvoked": False}
            write(file, good); oracle.replay_refusal_check(root, {"id": "authored"})
            for key, value in [("operationId", "other"), ("code", "Unknown"), ("nativeDispatchInvoked", True)]:
                write(file, dict(good, **{key: value}))
                with self.assertRaisesRegex(oracle.VerificationError, "replay refusal"):
                    oracle.replay_refusal_check(root, {"id": "authored"})

    def test_reviewed_input_digest_accepts_each_retained_alias(self):
        digest = "a" * 64
        for approval in [{"reviewedInputSha256": digest}, {"inputSha256": digest}, {"reviewedInputSha256": digest, "inputSha256": digest}]:
            self.assertEqual(digest, oracle.reviewed_input_digest(approval))

    def test_reviewed_input_digest_rejects_conflicting_aliases(self):
        with self.assertRaisesRegex(oracle.VerificationError, "aliases conflict"):
            oracle.reviewed_input_digest({"reviewedInputSha256": "a" * 64, "inputSha256": "b" * 64})

    def test_reviewed_input_digest_rejects_missing_or_malformed_aliases(self):
        for approval in [{}, {"inputSha256": None}, {"inputSha256": "hash"}, {"reviewedInputSha256": "a" * 64, "inputSha256": ""}]:
            with self.assertRaises(oracle.VerificationError): oracle.reviewed_input_digest(approval)

    def test_owned_genuine_fixture_hashes_and_preserved_native_metadata(self):
        root = Path(__file__).resolve().parents[1] / "fixtures/watch-tables/native-owned-v21"
        manifest = oracle.load(root / "manifest.json")
        self.assertTrue(manifest["sourceOwnedScratch"]); self.assertTrue(manifest["exportBytesUnmodified"])
        self.assertEqual(7, len(manifest["files"]))
        for entry in manifest["files"]:
            data = (root / entry["path"]).read_bytes()
            self.assertEqual(entry["sha256"], oracle.sha(data)); self.assertEqual(entry["bytes"], len(data))
            tree = oracle.xml(data); self.assertEqual("WithDefaults", tree.findtext("DocumentInfo/ExportSetting"))
            self.assertEqual("V21 Update 2", tree.findtext("DocumentInfo/InstalledProducts/Product/DisplayVersion"))
            self.assertNotIn(b"D:\\", data); self.assertNotIn(b"C:\\", data)

    def test_owned_native_target_lifecycle_retains_every_byte_except_time_and_literal(self):
        root = Path(__file__).resolve().parents[1] / "fixtures/watch-tables/native-owned-v21"
        baseline = (root / "MilestoneWatch.before.xml").read_bytes(); oracle.target_oracle(baseline, "FALSE")
        for stage in ["applied", "reopened", "restored", "original"]:
            data = (root / ("MilestoneWatch." + stage + ".xml")).read_bytes()
            oracle.target_oracle(data, "FALSE" if stage == "restored" else "TRUE")
            masked = data.replace(b"<ModifyValue>TRUE</ModifyValue>", b"<ModifyValue>FALSE</ModifyValue>")
            self.assertEqual(oracle.without_created_bytes(baseline), oracle.without_created_bytes(masked))
        peer = oracle.xml((root / "UnchangedPeer.xml").read_bytes()); rows = peer.find(oracle.PREFIX + "PlcWatchTable/ObjectList")
        self.assertEqual(1, len(rows)); self.assertEqual("FALSE", rows[0].findtext("AttributeList/ModifyValue"))
        force = oracle.xml((root / "ForceTable.xml").read_bytes()).find(oracle.PREFIX + "PlcForceTable")
        self.assertEqual("Force table", force.findtext("AttributeList/Name")); self.assertIsNone(force.find("ObjectList"))


if __name__ == "__main__": unittest.main()
