"""Offline adversarial oracle checks; fabricated collector receipts are explicitly synthetic."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
import unittest

import verify_scope as oracle

ROOT = Path(__file__).resolve()
while not (ROOT / "fixtures/testsuite/native-owned-v21").is_dir():
    ROOT = ROOT.parent
FIXTURE = ROOT / "fixtures/testsuite/native-owned-v21"


class OracleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="testsuite-synthetic-oracle-")
        self.directory = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        self.result = self.fixture()

    def put(self, name, data):
        (self.directory / name).write_bytes(data)
        return {"path": name, "sha256": oracle.sha(data), "bytes": len(data)}

    def json_put(self, name, value):
        return self.put(name, json.dumps(value).encode())

    def fixture(self):
        result = {"mode": "inspect-system-scope", "completed": True, "processId": 101,
                  "nativeProcessStartTimeUtc": "synthetic-open-1", "projectPath": "D:\\synthetic\\project.ap21"}
        for flag in ("automaticReplay", "nativeMutationInvoked", "applicationExecutionInvoked", "systemExecutionInvoked", "styleExecutionInvoked", "endpointConnectionInvoked", "nativeQualified", "testsPassed", "effectiveStyleScopeVerified", "beforeModified", "afterModified"):
            result[flag] = False
        admission = {key: result[key] for key in ("processId", "nativeProcessStartTimeUtc", "projectPath")}
        for key in ("ownershipEvidence", "openObservationEvidence", "resourceAdmissionEvidence", "checkpointRetrievalEvidence"):
            admission[key] = self.json_put(key + ".json", {"synthetic": True, "purpose": key})
        result["projectOpenEvidenceSha256"] = admission["openObservationEvidence"]["sha256"]
        result["reviewedSource"] = self.put("reviewed-source.tst", (FIXTURE / "inputs/vendor-help-example.tst").read_bytes())
        admission["reviewedSource"] = result["reviewedSource"]
        admission["binarySha256"] = self.put("TestSuiteSystemScopeEvidence.exe", b"SYNTHETIC-NOT-NATIVE")['sha256']
        admission["sourceFiles"] = [self.put("source-Program.cs", b"synthetic source"), self.put("source-TestSuiteSystemScopeEvidence.csproj", b"synthetic project")]
        for item in admission["sourceFiles"]:
            item["path"] = item["path"].removeprefix("source-")
        typed = {"name": "SingleStep", "serverAddress": "opc.tcp://192.168.0.1:6", "folderPath": "",
                 "folderIsNull": False, "serverInterface": "StandardSIMATIC"}
        for key in ("typedBefore", "typedAfterFirstExport", "typedAfter"):
            result[key] = self.json_put(key + ".json", typed)
        for stage in ("definitionsBefore", "definitionsAfter"):
            records = []
            for family, extension in (("style", "xml"), ("application", "tat"), ("system", "tst")):
                data = (FIXTURE / "definitions" / (family + "." + extension)).read_bytes()
                records.append({"family": family, "name": oracle.EXPECTED[family][0], "artifact": self.put(stage + "-" + family + "." + extension, data)})
            result[stage] = records
        self.json_put("admission.json", admission)
        self.json_put("result.json", result)
        return result

    def rewrite(self):
        self.json_put("result.json", self.result)

    def observation(self):
        return oracle.observe(self.directory)

    def test_genuine_retained_loss_remains_failed(self):
        report = oracle.compare(self.observation())
        self.assertTrue(report["first"]["collectionConsistencyPassed"])
        self.assertFalse(report["scopePreservationPassed"])
        self.assertEqual(report["first"]["folderDiagnosis"], "currentNativeFolderAlreadyMissingBeforeThisExport")
        self.assertFalse(report["originalImportVersusExportCauseEstablished"])

    def test_empty_folder_reopen_never_repairs_reviewed_source(self):
        first = self.observation()
        reopened = deepcopy(first)
        reopened.update(processId=102, nativeProcessStartTimeUtc="synthetic-open-2", projectOpenEvidenceSha256="b" * 64)
        report = oracle.compare(first, reopened)
        self.assertTrue(report["freshReopenVerified"])
        self.assertFalse(report["scopePreservationPassed"])
        self.assertTrue(report["failingProfilesRemainDisabled"])
        self.assertFalse(report["restorationVerified"])

    def test_same_native_process_is_not_fresh_reopen(self):
        first = self.observation()
        with self.assertRaisesRegex(ValueError, "Fresh native process"):
            oracle.compare(first, deepcopy(first))

    def test_different_project_is_not_fresh_reopen(self):
        first = self.observation()
        other = deepcopy(first)
        other["projectPath"] = "D:\\synthetic\\other.ap21"
        with self.assertRaisesRegex(ValueError, "another project"):
            oracle.compare(first, other)

    def test_same_open_receipt_is_not_independent(self):
        first = self.observation()
        other = deepcopy(first)
        other["processId"] = 102
        with self.assertRaisesRegex(ValueError, "Independent open"):
            oracle.compare(first, other)

    def test_tampered_native_bytes_rejected(self):
        (self.directory / self.result["definitionsBefore"][2]["artifact"]["path"]).write_bytes(b"wrong")
        with self.assertRaisesRegex(ValueError, "Artifact bytes"):
            self.observation()

    def test_changed_peer_detected_even_with_updated_hash(self):
        record = self.result["definitionsAfter"][0]
        record["artifact"] = self.put("changed-peer.xml", b"<changed/>")
        self.rewrite()
        self.assertFalse(self.observation()["testSuiteDefinitionPeersUnchanged"])

    def test_missing_peer_rejected(self):
        self.result["definitionsAfter"].pop()
        self.rewrite()
        with self.assertRaisesRegex(ValueError, "three-family"):
            self.observation()

    def test_foreign_peer_identity_rejected(self):
        self.result["definitionsAfter"][1]["name"] = "foreign"
        self.rewrite()
        with self.assertRaisesRegex(ValueError, "Definition identity"):
            self.observation()

    def test_modified_project_rejected(self):
        self.result["afterModified"] = True
        self.rewrite()
        with self.assertRaisesRegex(ValueError, "afterModified"):
            self.observation()

    def test_collector_failure_is_not_completed_evidence(self):
        self.json_put("failure.json", {"synthetic": True})
        with self.assertRaisesRegex(ValueError, "Collector reported failure"):
            self.observation()

    def test_path_escape_rejected(self):
        with self.assertRaisesRegex(ValueError, "leaves evidence"):
            oracle.artifact(self.directory, {"path": "../outside", "sha256": "a" * 64, "bytes": 1})

    def test_null_is_retained_not_coerced_to_empty(self):
        for key in ("typedBefore", "typedAfterFirstExport", "typedAfter"):
            current = json.loads((self.directory / self.result[key]["path"]).read_bytes())
            current.update(folderPath=None, folderIsNull=True)
            self.result[key] = self.json_put(key + ".json", current)
        self.rewrite()
        result = self.observation()
        self.assertIsNone(result["typedScope"]["folderPath"])
        self.assertFalse(result["typedScopeAgreesWithExport"])
        self.assertFalse(result["reviewedScopePreserved"])

    def test_null_marker_disagreement_rejected(self):
        with self.assertRaisesRegex(ValueError, "Null folder"):
            oracle.typed(json.dumps({"name": "SingleStep", "folderIsNull": False, "folderPath": None}).encode())

    def test_duplicate_scope_property_rejected(self):
        data = (FIXTURE / "definitions/system.tst").read_bytes().replace(b'FOLDER_PATH : ""', b'FOLDER_PATH : ""\r\nFOLDER_PATH : "D:\\Simatic"')
        with self.assertRaisesRegex(ValueError, "duplicate property"):
            oracle.scope(data)

    def test_unknown_interface_profile_rejected(self):
        data = (FIXTURE / "definitions/system.tst").read_bytes().replace(b"Standard_SIMATIC", b"UserDefined")
        with self.assertRaisesRegex(ValueError, "Unreviewed interface"):
            oracle.scope(data)

    def test_current_nonempty_getter_export_loss_is_distinguished(self):
        for key in ("typedBefore", "typedAfterFirstExport", "typedAfter"):
            current = json.loads((self.directory / self.result[key]["path"]).read_bytes())
            current["folderPath"] = "D:\\Simatic"
            self.result[key] = self.json_put(key + ".json", current)
        self.rewrite()
        self.assertEqual(self.observation()["folderDiagnosis"], "nonemptyNativeFolderDiffersFromExport")

    def test_source_hash_change_rejected(self):
        self.result["reviewedSource"] = self.put("reviewed-source.tst", b"unreviewed")
        self.rewrite()
        with self.assertRaisesRegex(ValueError, "Reviewed input changed"):
            self.observation()


if __name__ == "__main__":
    unittest.main(verbosity=2)
