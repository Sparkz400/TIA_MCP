"""Adversarial synthetic receipt checks; all fixtures are authored, never native evidence."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET
import verify_probe as verify


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value), encoding="utf-8")


def file(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return {"path": str(path), "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}


def block_xml(name, kind):
    root = ET.Element("Document")
    block = ET.SubElement(root, "SW.Blocks." + kind, ID="0")
    attrs = ET.SubElement(block, "AttributeList")
    ET.SubElement(attrs, "Name").text = name
    ET.SubElement(attrs, "Number").text = "1"
    objects = ET.SubElement(block, "ObjectList")
    texts = ET.SubElement(objects, "MultilingualText", CompositionName="Comment")
    values = ET.SubElement(texts, "ObjectList")
    for culture, text in (("de-DE", "Deutsch {0}"), ("en-GB", "English {0}")):
        attrs = ET.SubElement(ET.SubElement(values, "MultilingualTextItem"), "AttributeList")
        ET.SubElement(attrs, "Culture").text = culture
        ET.SubElement(attrs, "Text").text = text
    return ET.tostring(root)


class ProbeAudit(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.folder = self.root / "export"
        self.project = str(self.root / "project.ap21")
        self.oracle = {"projectPath": self.project, "plcName": "PLC", "blocks": [
            {"name": name, "kind": kind, "number": 1, "comments": [
                {"culture": "de-DE", "text": "Deutsch {0}"}, {"culture": "en-GB", "text": "English {0}"}]}
            for name, kind in (("Function", "FC"), ("Instance", "FB"))]}
        oracle_row = file(self.root / "oracle.json", json.dumps(self.oracle).encode())
        self.checkpoint = file(self.root / "checkpoint.zap21", b"synthetic checkpoint; not native")
        self.before = self.make_snapshot(self.folder, "before", [])
        rows, mappings = [], []
        for block in self.oracle["blocks"]:
            for culture in ("de-DE", "en-GB"):
                for format in ("SCL", "SimaticML", "SimaticSD"):
                    folder = self.folder / "workspaces" / str(len(rows))
                    workspace = "Synthetic_" + str(len(rows))
                    mapping = {"identity": [{"Name": block["name"]}], "format": format, "directory": str(folder), "fileName": "fixture", "compareState": "Equal"}
                    row = {"block": block["name"], "culture": culture, "format": format, "directory": str(folder), "workspace": workspace,
                           "mapping": mapping, "workspaceLanguage": culture, "state": "exported", "files": []}
                    primary = "fixture." + {"SCL": "scl", "SimaticML": "xml", "SimaticSD": "s7dcl"}[format]
                    content = block_xml(block["name"], block["kind"]) if format == "SimaticML" else b"Deutsch {0} English {0}"
                    for name, data in ((primary, content), (".vci/workspace.vci.config", ("<WorkspaceLanguage>" + culture + "</WorkspaceLanguage>").encode())):
                        row["files"].append({**file(folder / name, data), "relativePath": name})
                    rows.append(row)
                    mappings.append({"identity": [{"Name": workspace}], "name": workspace, "root": str(folder),
                                     "culture": culture, "mappings": [mapping]})
            folder = self.folder / "direct" / block["name"]
            item = {**file(folder / "fixture.s7dcl", b"synthetic SD"), "relativePath": "fixture.s7dcl"}
            rows.append({"block": block["name"], "format": "SimaticSD", "route": "PlcBlock.ExportAsDocuments", "directory": str(folder), "state": "returned", "files": [item]})
        self.after = self.make_snapshot(self.folder, "after", mappings)
        self.receipt = {"mode": "export", "completed": True, "saved": True, "nativeImportQualified": False,
                        "wholeProjectPreservationQualified": False, "automaticReplay": False, "projectPath": self.project,
                        "plcName": "PLC", "processId": 123, "checkpoint": self.checkpoint, "oracle": oracle_row, "profiles": rows}
        write_json(self.folder / "result.json", self.receipt)
        write_json(self.folder / "oracle.json", self.oracle)
        self.admission = {key: self.receipt[key] for key in ("mode", "projectPath", "plcName", "processId", "checkpoint", "oracle")}
        write_json(self.folder / "admission.json", self.admission)

    def tearDown(self):
        self.temp.cleanup()

    def make_snapshot(self, directory, phase, mappings, project=None):
        snapshot = {"projectPath": project or self.project, "plcName": "PLC", "blocks": [],
                    "languages": {"active": ["de-DE", "en-GB"], "reference": "de-DE", "editing": "de-DE"},
                    "projectLanguageImportOptions": "DoNotActivateInActiveCultures", "workspaces": copy.deepcopy(mappings)}
        for b in self.oracle["blocks"]:
            snapshot["blocks"].append({**b, "identity": [{"Name": b["name"]}], "xml": file(directory / phase / (b["name"] + ".xml"), block_xml(b["name"], b["kind"]))})
        write_json(directory / (phase + ".json"), snapshot)
        return snapshot

    def make_capture(self, directory, project, workspaces):
        self.make_snapshot(directory, "before", workspaces, project)
        oracle = {**self.oracle, "projectPath": project}
        oracle_row = file(directory / "oracle.json", json.dumps(oracle).encode())
        receipt = {**self.receipt, "mode": "snapshot", "saved": False, "projectPath": project,
                   "processId": 456, "oracle": oracle_row}
        receipt.pop("profiles")
        write_json(directory / "result.json", receipt)
        write_json(directory / "admission.json", {key: receipt[key] for key in
                   ("mode", "projectPath", "plcName", "processId", "checkpoint", "oracle")})
        return receipt

    def revise_capture(self, directory, changes):
        for name in ("result", "admission"):
            path = directory / (name + ".json")
            write_json(path, {**verify.read_json(path), **changes})

    def rerecord(self):
        write_json(self.folder / "result.json", self.receipt)

    def test_matrix_scoped_only(self):
        result = verify.review(self.folder)
        self.assertEqual(14, len(result["profiles"]))
        self.assertEqual(4, sum(p["exactBlockCommentsPreserved"] for p in result["profiles"]))
        self.assertFalse(result["nativeImportEnabled"])
        self.assertTrue(result["actualLifecycleAndCleanupProofRequiredSeparately"])
        self.assertTrue(result["captureBindingsVerified"] and result["workspaceEffectsFullyAccounted"])

    def test_tampered_primary_hash(self):
        Path(self.receipt["profiles"][0]["files"][0]["path"]).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            verify.review(self.folder)

    def test_undeclared_companion(self):
        (Path(self.receipt["profiles"][0]["directory"]) / "unreviewed.s7res").write_bytes(b"opaque")
        with self.assertRaisesRegex(ValueError, "inventory mismatch"):
            verify.review(self.folder)

    def test_native_green_does_not_qualify_scl_or_sd(self):
        result = verify.review(self.folder)
        self.assertTrue(all(not p["exactBlockCommentsPreserved"] for p in result["profiles"] if p["format"] != "SimaticML"))

    def test_missing_profile(self):
        self.receipt["profiles"].pop()
        self.rerecord()
        with self.assertRaisesRegex(ValueError, "direct SD matrix"):
            verify.review(self.folder)

    def test_retained_workspace_language_mismatch(self):
        row = self.receipt["profiles"][0]
        row["files"][1] = {**file(Path(row["files"][1]["path"]), b"<WorkspaceLanguage>en-GB</WorkspaceLanguage>"),
                           "relativePath": ".vci/workspace.vci.config"}
        self.rerecord()
        with self.assertRaisesRegex(ValueError, "workspace culture differs"):
            verify.review(self.folder)

    def test_mapping_identity_mismatch(self):
        self.receipt["profiles"][0]["mapping"]["identity"] = [{"Name": "different PLC same block"}]
        self.rerecord()
        with self.assertRaisesRegex(ValueError, "Mapped native object"):
            verify.review(self.folder)

    def test_settings_changed(self):
        self.after["projectLanguageImportOptions"] = "ActivateInActiveCultures"
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "peers changed"):
            verify.review(self.folder)

    def test_peer_full_xml_changed_with_retained_comments(self):
        path = Path(self.after["blocks"][0]["xml"]["path"])
        altered = path.read_bytes().replace(b'ID="0"', b'ID="99"')
        self.after["blocks"][0]["xml"] = file(path, altered)
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "peers changed"):
            verify.review(self.folder)

    def test_wrong_project_binding(self):
        self.receipt["projectPath"] = "unreviewed.ap21"
        self.rerecord()
        with self.assertRaisesRegex(ValueError, "project/PLC"):
            verify.review(self.folder)

    def test_export_snapshot_project_and_plc_bindings(self):
        for phase in ("before", "after"):
            path = self.folder / (phase + ".json")
            original = verify.read_json(path)
            for field, value in (("projectPath", str(self.root / "different.ap21")), ("plcName", "OtherPLC")):
                with self.subTest(phase=phase, field=field):
                    write_json(path, {**original, field: value})
                    with self.assertRaisesRegex(ValueError, "Snapshot project/PLC binding"):
                        verify.review(self.folder)
            write_json(path, original)

    def test_missing_snapshot_identity_refused(self):
        self.after.pop("projectPath")
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "Snapshot project/PLC identity missing"):
            verify.review(self.folder)

    def test_result_admission_binding_disagreements(self):
        for field, value, message in (("processId", 999, "process binding"),
                                      ("checkpoint", {}, "checkpoint binding"), ("oracle", {}, "oracle binding")):
            with self.subTest(field=field):
                write_json(self.folder / "admission.json", {**self.admission, field: value})
                with self.assertRaisesRegex(ValueError, message):
                    verify.review(self.folder)
        write_json(self.folder / "admission.json", self.admission)

    def test_unreviewed_workspace_addition(self):
        self.after["workspaces"].append({"name": "Unreviewed", "identity": [{"Name": "Unreviewed"}],
                                        "root": str(self.root / "unreviewed"), "culture": "de-DE", "mappings": []})
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "Unreviewed or missing workspace effect"):
            verify.review(self.folder)

    def test_missing_workspace_effect(self):
        self.after["workspaces"].pop()
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "Unreviewed or missing workspace effect"):
            verify.review(self.folder)

    def test_preexisting_workspace_preserved_and_metadata_change_refused(self):
        existing = {"name": "Existing", "identity": [{"Name": "Existing"}], "root": str(self.root / "existing"),
                    "culture": "de-DE", "mappings": []}
        self.before["workspaces"].append(copy.deepcopy(existing))
        self.after["workspaces"].append(copy.deepcopy(existing))
        write_json(self.folder / "before.json", self.before)
        write_json(self.folder / "after.json", self.after)
        self.assertTrue(verify.review(self.folder)["workspaceEffectsFullyAccounted"])
        self.after["workspaces"][-1]["culture"] = "en-GB"
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "Preexisting VCI mapping changed"):
            verify.review(self.folder)

    def test_export_workspace_reused(self):
        self.before["workspaces"].append(copy.deepcopy(self.after["workspaces"][0]))
        write_json(self.folder / "before.json", self.before)
        with self.assertRaisesRegex(ValueError, "Export workspace identity reused"):
            verify.review(self.folder)

    def test_workspace_snapshot_root_and_culture(self):
        original = copy.deepcopy(self.after["workspaces"][0])
        for field, value in (("root", str(self.root / "other")), ("culture", "en-GB")):
            with self.subTest(field=field):
                self.after["workspaces"][0] = {**original, field: value}
                write_json(self.folder / "after.json", self.after)
                with self.assertRaisesRegex(ValueError, "Saved workspace root/culture differs"):
                    verify.review(self.folder)

    def test_duplicate_native_workspace_identity(self):
        self.after["workspaces"].append(copy.deepcopy(self.after["workspaces"][0]))
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "Duplicate native workspace identity"):
            verify.review(self.folder)

    def test_workspace_identity_must_name_saved_workspace(self):
        self.after["workspaces"][0]["identity"] = [{"Name": "OtherWorkspace"}]
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "Workspace native identity/name mismatch"):
            verify.review(self.folder)

    def test_overlapping_profile_directories(self):
        self.receipt["profiles"][-1]["directory"] = self.receipt["profiles"][0]["directory"]
        self.rerecord()
        with self.assertRaisesRegex(ValueError, "Overlapping profile directories"):
            verify.review(self.folder)

    def test_failed_export_is_reported_but_unrecorded_mapping_is_refused(self):
        row = self.receipt["profiles"][0]
        row["state"] = "failed"
        row.pop("mapping")
        self.after["workspaces"][0]["mappings"] = []
        self.rerecord()
        write_json(self.folder / "after.json", self.after)
        self.assertFalse(verify.review(self.folder)["allExportsReturned"])
        self.after["workspaces"][0]["mappings"] = [{"identity": [{"Name": "UndeclaredMapping"}]}]
        write_json(self.folder / "after.json", self.after)
        with self.assertRaisesRegex(ValueError, "Failed export mapping effect unreviewed"):
            verify.review(self.folder)

    def test_rich_and_duplicate_native_comment(self):
        valid = block_xml("Function", "FC")
        with self.assertRaisesRegex(ValueError, "rich comment"):
            verify.comments(verify.native_block(valid.replace(b"Deutsch {0}", b"<Run>Deutsch {0}</Run>"))[0])
        with self.assertRaisesRegex(ValueError, "Duplicate comment culture"):
            verify.comments(verify.native_block(valid.replace(b"en-GB", b"de-DE"))[0])

    def test_dtd_and_duplicate_json_refused(self):
        with self.assertRaisesRegex(ValueError, "DTD/entity"):
            verify.native_block(b'<!DOCTYPE Document [<!ENTITY e SYSTEM "file:///secret">]><Document/>')
        write_json(self.root / "dup.json", {})
        (self.root / "dup.json").write_text('{"x":1,"x":2}')
        with self.assertRaisesRegex(ValueError, "Duplicate JSON"):
            verify.read_json(self.root / "dup.json")

    def test_reopen_and_restoration_are_snapshot_claims_only(self):
        reopened, restored = self.root / "reopened", self.root / "restored"
        self.make_capture(reopened, self.project, self.after["workspaces"])
        self.make_capture(restored, str(self.root / "restoredProject.ap21"), self.before["workspaces"])
        result = verify.review(self.folder, reopened, restored)
        self.assertTrue(result["reopenedSnapshotMatches"] and result["restoredSnapshotMatches"])
        self.assertFalse(result["fullNativeQualification"])
        self.make_capture(restored, self.project, self.before["workspaces"])
        with self.assertRaisesRegex(ValueError, "new project destination"):
            verify.review(self.folder, reopened, restored)

    def test_reopen_of_different_project_with_identical_peers_refused(self):
        reopened = self.root / "reopened"
        self.make_capture(reopened, str(self.root / "unrelated.ap21"), self.after["workspaces"])
        with self.assertRaisesRegex(ValueError, "exact exported project"):
            verify.review(self.folder, reopened)

    def test_verification_snapshot_project_and_plc_bindings(self):
        for phase, workspaces in (("reopened", self.after["workspaces"]), ("restored", self.before["workspaces"])):
            directory = self.root / phase
            project = self.project if phase == "reopened" else str(self.root / "restore.ap21")
            self.make_capture(directory, project, workspaces)
            path = directory / "before.json"
            original = verify.read_json(path)
            for field, value in (("projectPath", str(self.root / "unrelated.ap21")), ("plcName", "OtherPLC")):
                with self.subTest(phase=phase, field=field):
                    write_json(path, {**original, field: value})
                    with self.assertRaisesRegex(ValueError, "Snapshot project/PLC binding"):
                        verify.review(self.folder, **{phase: directory})

    def test_verification_receipt_requires_admission_and_qualification_flags(self):
        reopened = self.root / "reopened"
        receipt = self.make_capture(reopened, self.project, self.after["workspaces"])
        write_json(reopened / "result.json", {**receipt, "nativeImportQualified": True})
        with self.assertRaisesRegex(ValueError, "cannot promote"):
            verify.review(self.folder, reopened)
        write_json(reopened / "result.json", receipt)
        admission = verify.read_json(reopened / "admission.json")
        write_json(reopened / "admission.json", {**admission, "processId": 999})
        with self.assertRaisesRegex(ValueError, "process binding"):
            verify.review(self.folder, reopened)

    def test_verification_checkpoint_and_oracle_must_match_export(self):
        for phase, workspaces in (("reopened", self.after["workspaces"]), ("restored", self.before["workspaces"])):
            directory = self.root / phase
            project = self.project if phase == "reopened" else str(self.root / "restore.ap21")
            with self.subTest(phase=phase, field="checkpoint"):
                self.make_capture(directory, project, workspaces)
                checkpoint = file(directory / "different.zap21", b"another synthetic checkpoint")
                self.revise_capture(directory, {"checkpoint": checkpoint})
                with self.assertRaisesRegex(ValueError, "Verification checkpoint binding differs"):
                    verify.review(self.folder, **{phase: directory})
            with self.subTest(phase=phase, field="oracle"):
                self.make_capture(directory, project, workspaces)
                oracle = verify.read_json(directory / "oracle.json")
                oracle["blocks"][0]["comments"][0]["text"] = "Changed expectation"
                artifact = file(directory / "oracle.json", json.dumps(oracle).encode())
                self.revise_capture(directory, {"oracle": artifact})
                with self.assertRaisesRegex(ValueError, "Verification oracle content differs"):
                    verify.review(self.folder, **{phase: directory})


if __name__ == "__main__":
    unittest.main(verbosity=2)
