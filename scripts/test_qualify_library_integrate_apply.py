"""Offline checks of the combined-apply runner's pure oracles; nothing starts TIA or the Host."""
import hashlib
from pathlib import Path
import tempfile
import unittest

from block_test_runtime import NativeToolError
import qualify_library_integrate_apply as subject

MANIFEST = {"root": "Gate", "instance": "Gate_1", "dependency": "State", "caller": "Caller"}
PIN = {"typeGuid": "43B5C338-B589-4AB5-8C82-CAE3E08E9D06", "versionGuid": "0f8fad5b-d9cb-469f-a165-70867728950e"}


def eligible_preview():
    return {"applySupported": True, "combinedApply": {"enabled": True, "eligible": True, "refusal": None},
            "effects": [{"action": "library_instantiate"}, {"action": "applyPlcComponentSteps", "nativeMutation": True, "steps": 2}]}


class Previews(unittest.TestCase):
    def test_eligible_preview_requires_exact_combined_effect(self):
        self.assertTrue(subject.require_combined_preview(eligible_preview(), True, eligible=True)["eligible"])
        preview = eligible_preview(); preview["effects"][1]["steps"] = 1
        with self.assertRaisesRegex(RuntimeError, "two-step"):
            subject.require_combined_preview(preview, True, eligible=True)

    def test_refused_preview_names_its_reason(self):
        preview = {"applySupported": False, "combinedApply": {"enabled": True, "eligible": False, "refusal": "OB insertion is refused; add it."}}
        subject.require_combined_preview(preview, True, refusal="OB insertion is refused")
        with self.assertRaisesRegex(RuntimeError, "Unexpected combined-apply refusal"):
            subject.require_combined_preview(preview, True, refusal="Only a new or existing FBD FC caller")
        with self.assertRaisesRegex(RuntimeError, "eligibility differs"):
            subject.require_combined_preview(preview, True, eligible=True)

    def test_flag_off_preview_and_capabilities_stay_plan_only(self):
        self.assertIsNone(subject.require_combined_preview({"applySupported": False}, False))
        with self.assertRaisesRegex(RuntimeError, "plan-only"):
            subject.require_combined_preview(eligible_preview(), False)
        subject.require_capabilities({"plcIntegration": {"applySupported": False}}, False)
        subject.require_capabilities({"plcIntegration": {"applySupported": True, "combinedApply": "Enabled"}}, True)
        with self.assertRaisesRegex(RuntimeError, "Host flag"):
            subject.require_capabilities({"plcIntegration": {"applySupported": True, "combinedApply": "Enabled"}}, False)

    def test_refusal_requires_the_exact_error_code(self):
        error = NativeToolError("change_apply", {"error": {"code": "PlcIntegrationNotQualified", "message": "plan only"}})
        self.assertEqual("plan only", subject.refusal(error)["message"])
        with self.assertRaisesRegex(RuntimeError, "Expected PlcIntegrationNotQualified"):
            subject.refusal(NativeToolError("change_apply", {"error": {"code": "IdempotencyConflict"}}))
        with self.assertRaisesRegex(RuntimeError, "Expected"):
            subject.refusal(RuntimeError("lost"))


class Receipts(unittest.TestCase):
    def receipt(self, folder):
        archive = Path(folder) / "before.zap21"; archive.write_bytes(b"initial-checkpoint")
        checkpoint = {"kind": "project", "originalPath": "C:/p/P.ap21", "archivePath": str(archive), "sha256": hashlib.sha256(b"initial-checkpoint").hexdigest()}
        journal = {"engineeringVerified": True, "checkpoints": [checkpoint], "combinedApply": {"state": "installation_verified", "failedStep": None}}
        return {"state": "applied", "changeId": "lib-1", "previewHash": "h", "journal": journal}, {"changeId": "lib-1", "previewHash": "h"}

    def test_verified_installation_returns_its_single_checkpoint(self):
        with tempfile.TemporaryDirectory() as folder:
            applied, prepared = self.receipt(folder)
            self.assertEqual("project", subject.verify_combined_receipt(applied, prepared, "C:/p/P.ap21")["kind"])

    def test_failed_step_foreign_project_or_changed_archive_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            for change, message in ((lambda a: a["journal"]["combinedApply"].update(failedStep="apply_graphical_typed_edit"), "verified installation"),
                                    (lambda a: a["journal"].update(failure={"code": "X"}), "verified installation"),
                                    (lambda a: a["journal"]["checkpoints"].append(dict(a["journal"]["checkpoints"][0])), "exactly one"),
                                    (lambda a: Path(a["journal"]["checkpoints"][0]["archivePath"]).write_bytes(b"x"), "archive bytes")):
                applied, prepared = self.receipt(folder)
                change(applied)
                with self.subTest(message=message), self.assertRaisesRegex(RuntimeError, message):
                    subject.verify_combined_receipt(applied, prepared, "C:/p/P.ap21")
            applied, prepared = self.receipt(folder)
            with self.assertRaisesRegex(RuntimeError, "exactly one"):
                subject.verify_combined_receipt(applied, prepared, "C:/p/Other.ap21")


class Inventory(unittest.TestCase):
    PLAN = {"fb": {"path": "/plc/blocks/Gate"}, "instanceDb": {"path": "/plc/blocks/Gate_1"}, "caller": {"blockPath": "/plc/blocks/Caller"}}

    def test_planned_objects_include_a_new_caller_only(self):
        self.assertEqual({"block:/plc/blocks/Gate", "block:/plc/blocks/Gate_1", "block:/plc/blocks/Caller"}, subject.planned_objects(self.PLAN, False))
        self.assertEqual({"block:/plc/blocks/Gate", "block:/plc/blocks/Gate_1"}, subject.planned_objects(self.PLAN, True))

    def test_version_key_normalizes_guid_spelling(self):
        upper = {key: value.upper() for key, value in PIN.items()}
        self.assertEqual(subject.version_key("Gate", PIN), subject.version_key("Gate", upper))

    def test_instance_db_identity_and_number(self):
        xml = ("<Document><SW.Blocks.InstanceDB ID='0'><AttributeList><InstanceOfName>Gate</InstanceOfName><InstanceOfType>FB</InstanceOfType>"
               "<Name>Gate_1</Name><Number>2</Number></AttributeList></SW.Blocks.InstanceDB></Document>")
        subject.verify_instance_db(xml, MANIFEST, 2)
        with self.assertRaisesRegex(RuntimeError, "first available number"):
            subject.verify_instance_db(xml, MANIFEST, 3)
        with self.assertRaisesRegex(RuntimeError, "one instance DB"):
            subject.verify_instance_db(xml.replace("InstanceDB", "GlobalDB"), MANIFEST, 2)


class ObFixture(unittest.TestCase):
    MAIN = ('<Document><SW.Blocks.OB ID="0"><AttributeList><Name>Main</Name><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage>'
            '</AttributeList><ObjectList><SW.Blocks.CompileUnit ID="3"><AttributeList><NetworkSource><StructuredText>x</StructuredText>'
            '</NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.OB></Document>')
    PEER = ('<NetworkSource><FlgNet><Parts><Access Scope="LocalVariable" UId="21">\n <Symbol>\n  <Component Name="PreserveInput" />'
            '</Symbol></Access><Access Scope="LocalVariable" UId="22"><Symbol><Component Name="PreserveOutput" /></Symbol></Access>'
            '<Part Name="Coil" UId="23" /></Parts></FlgNet></NetworkSource>')

    def test_ob_takes_main_envelope_and_peer_network_bound_to_global_tags(self):
        xml = subject.fbd_ob(self.MAIN, self.PEER, "ComponentEnable", "ComponentActive")
        self.assertIn("<Name>TiaMcpComponentOb</Name><Number>123</Number>", xml)
        self.assertEqual(2, xml.count("<ProgrammingLanguage>FBD</ProgrammingLanguage>"))
        self.assertNotIn("StructuredText", xml)
        self.assertNotIn("LocalVariable", xml)
        self.assertIn('<Component Name="ComponentEnable" />', xml)
        self.assertIn('<Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="ComponentActive" />', xml)

    def test_unexpected_exports_fail_closed(self):
        with self.assertRaisesRegex(RuntimeError, "PreserveOutput exactly once"):
            subject.fbd_ob(self.MAIN, self.PEER.replace("PreserveOutput", "Other"), "A", "B")
        with self.assertRaisesRegex(RuntimeError, "one network"):
            subject.fbd_ob(self.MAIN, self.PEER + self.PEER, "A", "B")
        with self.assertRaisesRegex(RuntimeError, "<Number>1</Number>"):
            subject.fbd_ob(self.MAIN.replace("<Number>1</Number>", "<Number>7</Number>"), self.PEER, "A", "B")


if __name__ == "__main__":
    unittest.main()
