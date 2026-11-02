"""Offline checks of the combined-apply runner's pure oracles; nothing starts TIA or the Host."""
import hashlib
from pathlib import Path
import tempfile
import unittest

from block_test_runtime import NativeToolError
import qualify_library_integrate_apply as subject

MANIFEST = {"root": "Gate", "instance": "Gate_1", "dependency": "State", "caller": "Caller"}
PIN = {"typeGuid": "43B5C338-B589-4AB5-8C82-CAE3E08E9D06", "versionGuid": "0f8fad5b-d9cb-469f-a165-70867728950e"}


def eligible_preview(steps=2):
    return {"applySupported": True, "combinedApply": {"enabled": True, "eligible": True, "refusal": None}, "integrationPlan": {"steps": [{}] * steps},
            "effects": [{"action": "library_instantiate"}, {"action": "applyPlcComponentSteps", "nativeMutation": True, "steps": steps}]}


class Previews(unittest.TestCase):
    def test_eligible_preview_requires_exact_combined_effect(self):
        self.assertTrue(subject.require_combined_preview(eligible_preview(), eligible=True)["eligible"])
        preview = eligible_preview(); preview["effects"][1]["steps"] = 1
        with self.assertRaisesRegex(RuntimeError, "exact combined effect"):
            subject.require_combined_preview(preview, eligible=True)
        # A reused FB has one typed step and no import step.
        self.assertTrue(subject.require_combined_preview(eligible_preview(1), eligible=True)["eligible"])

    def test_refused_preview_names_its_reason(self):
        preview = {"applySupported": False, "combinedApply": {"enabled": True, "eligible": False, "refusal": "OB insertion is refused; add it."}}
        subject.require_combined_preview(preview, refusal="OB insertion is refused")
        with self.assertRaisesRegex(RuntimeError, "Unexpected combined-apply refusal"):
            subject.require_combined_preview(preview, refusal=subject.FB_REFUSAL)
        with self.assertRaisesRegex(RuntimeError, "eligibility differs"):
            subject.require_combined_preview(preview, eligible=True)

    def test_default_host_must_report_combined_apply(self):
        # Combined apply is the default: the old plan-only preview and capabilities are now failures.
        for expectation in ({"eligible": True}, {"refusal": subject.FB_REFUSAL}):
            with self.subTest(expectation=expectation), self.assertRaisesRegex(RuntimeError, "eligibility differs"):
                subject.require_combined_preview({"applySupported": False}, **expectation)
        subject.require_capabilities({"plcIntegration": {"applySupported": True, "combinedApply": "Enabled"}})
        for plan_only in ({"applySupported": False}, {"applySupported": True}, {}):
            with self.subTest(plan_only=plan_only), self.assertRaisesRegex(RuntimeError, "default combined apply"):
                subject.require_capabilities({"plcIntegration": plan_only})

    def test_refusal_requires_the_exact_error_code(self):
        error = NativeToolError("change_apply", {"error": {"code": "PlcIntegrationNotQualified", "message": "plan only"}})
        self.assertEqual("plan only", subject.refusal(error)["message"])
        with self.assertRaisesRegex(RuntimeError, "Expected PlcIntegrationNotQualified"):
            subject.refusal(NativeToolError("change_apply", {"error": {"code": "IdempotencyConflict"}}))
        with self.assertRaisesRegex(RuntimeError, "Expected"):
            subject.refusal(RuntimeError("lost"))


class ScratchLanguages(unittest.TestCase):
    @staticmethod
    def apply(*after):
        calls, states = [], list(after)
        def apply(kind, payload):
            calls.append((kind, payload))
            return {"preview": {"effects": [{"languageSettings": {"after": states.pop(0)}}]}}
        return calls, apply

    def test_other_active_languages_are_removed_in_a_second_change(self):
        calls, apply = self.apply({"active": ["de-DE", "en-US"], "reference": "de-DE", "editing": "de-DE"},
                                  {"active": ["de-DE"], "reference": "de-DE", "editing": "de-DE"})
        subject.german_only_scratch(apply)
        self.assertEqual([("project_languages_set", {"addActive": ["de-DE"], "referenceLanguage": "de-DE", "editingLanguage": "de-DE"}),
                          ("project_languages_set", {"removeActive": ["en-US"]})], calls)

    def test_german_only_scratch_needs_one_change_and_any_other_end_state_fails(self):
        calls, apply = self.apply({"active": ["de-DE"], "reference": "de-DE", "editing": "de-DE"})
        subject.german_only_scratch(apply)
        self.assertEqual(1, len(calls))
        _, apply = self.apply({"active": ["de-DE", "en-US"], "reference": "de-DE", "editing": "de-DE"},
                              {"active": ["de-DE", "en-US"], "reference": "de-DE", "editing": "de-DE"})
        with self.assertRaisesRegex(RuntimeError, "not exactly de-DE"):
            subject.german_only_scratch(apply)


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
    PLAN = {"fb": {"path": "/plc/blocks/Gate", "reused": False}, "instanceDb": {"path": "/plc/blocks/Gate_1"}, "caller": {"blockPath": "/plc/blocks/Caller"}}

    def test_planned_objects_include_a_new_caller_only(self):
        self.assertEqual({"block:/plc/blocks/Gate", "block:/plc/blocks/Gate_1", "block:/plc/blocks/Caller"}, subject.planned_objects(self.PLAN, False))
        self.assertEqual({"block:/plc/blocks/Gate", "block:/plc/blocks/Gate_1"}, subject.planned_objects(self.PLAN, True))

    def test_reuse_adds_no_fb_and_a_multi_instance_no_block(self):
        reused = dict(self.PLAN, fb={"path": "/plc/blocks/Gate", "reused": True})
        self.assertEqual({"block:/plc/blocks/Gate_1", "block:/plc/blocks/Caller"}, subject.planned_objects(reused, False))
        multi = {key: value for key, value in reused.items() if key != "instanceDb"}
        self.assertEqual(set(), subject.planned_objects(multi, True))

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


class Candidates(unittest.TestCase):
    TEMPLATE = Path(subject.__file__).resolve().parents[1] / "fixtures/typed-graphical/native/Tgp_TemplateFC_FBD.xml"

    def test_two_network_caller_renumbers_every_id_consecutively(self):
        import re
        xml = subject.two_network_fc(self.TEMPLATE.read_text(encoding="utf-8-sig"), "Twice", 77)
        self.assertEqual(2, xml.count("<SW.Blocks.CompileUnit "))
        ids = [int(value, 16) for value in re.findall(r'(?<![A-Za-z])ID="([0-9A-F]+)"', xml)]
        self.assertEqual(list(range(ids[0], ids[0] + len(ids))), ids)
        self.assertEqual(1, xml.count("<Name>Twice</Name>")); self.assertEqual(1, xml.count("<Number>77</Number>"))
        with self.assertRaisesRegex(RuntimeError, "exactly one network"):
            subject.two_network_fc(xml, "Thrice", 78)

    def test_candidate_aggregation_accepts_installs_and_the_parent_refusal_only(self):
        # The captured component effect lets the caller's own instance DB install; only the embedding parent must end refused,
        # and an install there would be a failure too.
        self.assertEqual({}, subject.failed_candidates({"ladStandalone": "installed", "multiInstanceDependents": "installed", "multiInstanceParent": "refused"}))
        self.assertEqual({"multiInstanceDependents": "refused", "fbCaller": "failed: x", "multiInstanceParent": "installed"},
                         subject.failed_candidates({"multiInstanceDependents": "refused", "fbCaller": "failed: x", "reuse": "installed", "multiInstanceParent": "installed"}))
        self.assertEqual({"reuse": "refused"}, subject.failed_candidates({"reuse": "refused"}))

    def test_admission_records_count_open_and_archived_records(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertEqual([], subject.admission_records(root))
            (root / "engineering-admissions" / "a1").mkdir(parents=True); (root / "engineering-admissions" / ".archive" / "a0").mkdir(parents=True)
            (root / "engineering-admissions" / "note.json").write_text("{}", encoding="utf-8")
            self.assertEqual(["a0", "a1"], subject.admission_records(root))

    def test_refusal_and_default_host_cases_start_from_the_base_manifest(self):
        # ir1 (1 November): refusals() left multiInstance set, so default_host's standalone FC plan became a multi-instance plan.
        class Staged(Exception): pass
        for method in ("refusals", "default_host"):
            with tempfile.TemporaryDirectory() as directory, self.subTest(method=method):
                run = object.__new__(subject.IntegrateApplyRun)
                run.base = {"callerMode": "existing", "root": "Gate"}
                run.manifest = {**run.base, "multiInstance": subject.MULTI_NAME}
                run.output, run.report, run.persist = Path(directory), {"cases": {}}, lambda: None
                run.session = type("Session", (), {"client_factory": None})()
                seen = []
                def stage(archive, released): seen.append(dict(run.manifest)); raise Staged()
                run.stage_consumer = stage
                with self.assertRaises(Staged):
                    getattr(run, method)("archive", {})
                self.assertEqual([{"callerMode": "createStandaloneFc", "root": "Gate"}], seen)

    def test_refused_apply_requires_the_pre_admission_refusal_and_no_admission_record(self):
        prepared = {"changeId": "lib-" + "a" * 32, "previewHash": "b" * 64}
        def run_with(message, admit=False):
            directory = tempfile.mkdtemp()
            run = object.__new__(subject.IntegrateApplyRun)
            run.output = Path(directory)
            def call(tool, arguments=None):
                if tool == "change_apply":
                    if admit: (run.output / "engineering-admissions" / "x1").mkdir(parents=True)
                    raise NativeToolError(tool, {"error": {"code": subject.REFUSED, "message": message}})
                return {"state": "prepared", "journal": {}}
            run.call = call
            return run.refused_apply(prepared)
        self.assertEqual(subject.REFUSED, run_with(subject.BEFORE_ADMISSION + "OB insertion is refused.")["code"])
        for message, admit in (("Combined apply refused: OB insertion is refused.", False), (subject.BEFORE_ADMISSION + "x", True)):
            with self.subTest(message=message, admit=admit), self.assertRaises(RuntimeError):
                run_with(message, admit)

    def test_host_instance_db_source_is_one_db_of_the_host_fb(self):
        source = subject.HOST_DB_SOURCE
        self.assertEqual(['DATA_BLOCK "TiaMcpComponentHost_DB"', '"TiaMcpComponentHost"'], [line for line in source.splitlines() if '"' in line])
        self.assertEqual({"multiInstanceParent"}, subject.REFUSED_CASES)
        self.assertEqual(['FUNCTION_BLOCK "TiaMcpComponentParent"', ' Host : "TiaMcpComponentHost";'], [line for line in subject.PARENT_SOURCE.splitlines() if '"' in line])
        self.assertNotIn(subject.PARENT_FB_NUMBER, (2, subject.HOST_FB_NUMBER))

    def test_dependent_db_gains_exactly_the_captured_member(self):
        def db(*members):
            body = "".join('<Member Name="%s" Datatype="%s"><Sections><Section Name="Static"><Member Name="State" Datatype="&quot;S&quot;" /></Section></Sections></Member>'
                           % (name, datatype) for name, datatype in members)
            return ('<Document><DocumentInfo><Created>%s</Created></DocumentInfo><SW.Blocks.InstanceDB ID="0"><AttributeList><Interface>'
                    '<Sections xmlns="urn:i"><Section Name="Input" /><Section Name="Static">%s</Section></Sections></Interface>'
                    '<Name>H_DB</Name></AttributeList></SW.Blocks.InstanceDB></Document>') % (len(members), body)
        gate = ("Gate", "&quot;TiaMcpComponentGate&quot;")
        subject.verify_dependent_db(db(), db(gate), db(("Marker", "Int"), gate), "Gate")
        for name, before, after in (("other member", db(), db(("Gate", "Int"))), ("missing member", db(), db()),
                                    ("changed baseline", db(("Marker", "Int")), db(("Marker", "Bool"), gate))):
            with self.subTest(name=name), self.assertRaises(RuntimeError):
                subject.verify_dependent_db(before, after, db(gate), "Gate")

    def test_runner_path_check_matches_the_worker_layout(self):
        # PlcComponentQualificationGate.CombinedApplyArchivePathLength: 121 characters for a 51-character root.
        self.assertEqual(121, 51 + subject.COMBINED_PATH_OVERHEAD)
        self.assertNotEqual(2, subject.HOST_FB_NUMBER)

    def test_candidate_cases_name_only_gate_features(self):
        gate = {"ladCaller", "fbCaller", "obCaller", "multiInstance", "reusedComponent", "earlierPlacement"}
        for name, (overrides, features) in subject.CANDIDATES.items():
            with self.subTest(name=name):
                self.assertTrue(set(features) <= gate)
                self.assertIn(overrides["callerMode"], subject.MODES)
        for path in ("callers/TiaMcpComponentCaller.lad.s7dcl", "callers/TiaMcpComponentHost.s7dcl"):
            self.assertTrue((subject.FIXTURES / path).is_file(), path)

    def test_combinations_name_exactly_their_gate_features(self):
        # PlcComponentQualificationGate.Features: LAD language, an FB caller, a multi-instance, a reused FB, an earlier network.
        verified = {"dependentInstanceDb+earlierPlacement+fbCaller+ladCaller+multiInstance+reusedComponent", "dependentInstanceDb+earlierPlacement+fbCaller+multiInstance+reusedComponent", "dependentInstanceDb+fbCaller+ladCaller+multiInstance+reusedComponent", "dependentInstanceDb+fbCaller+multiInstance+reusedComponent", "earlierPlacement", "earlierPlacement+fbCaller", "earlierPlacement+fbCaller+ladCaller", "earlierPlacement+fbCaller+ladCaller+multiInstance+reusedComponent", "earlierPlacement+fbCaller+ladCaller+reusedComponent", "earlierPlacement+fbCaller+multiInstance+reusedComponent", "earlierPlacement+fbCaller+reusedComponent", "earlierPlacement+ladCaller", "earlierPlacement+ladCaller+reusedComponent", "earlierPlacement+reusedComponent", "fbCaller", "fbCaller+ladCaller", "fbCaller+ladCaller+multiInstance+reusedComponent", "fbCaller+ladCaller+reusedComponent", "fbCaller+multiInstance+reusedComponent", "fbCaller+reusedComponent", "ladCaller", "ladCaller+reusedComponent", "reusedComponent"}
        keys = set()
        for name, (reuse, caller, features) in subject.COMBINATIONS.items():
            with self.subTest(name=name):
                derived = sorted(feature for feature, present in (("ladCaller", caller["language"] == "LAD"), ("fbCaller", caller["kind"] == "FB"),
                                 ("multiInstance", caller.get("multi")), ("reusedComponent", reuse), ("earlierPlacement", caller.get("networks") == 2),
                                 ("dependentInstanceDb", caller.get("dependents"))) if present)
                self.assertEqual(features, [] if "+".join(derived) in verified else derived)
                self.assertTrue(not caller.get("multi") or reuse and caller["kind"] == "FB", "A multi-instance needs a reused FB in an FB caller")
                self.assertTrue(not caller.get("dependents") or caller.get("multi"), "Only a multi-instance changes the caller's instance DB")
                self.assertTrue(not caller.get("new") or caller["kind"] == "FC" and caller.get("networks", 1) == 1)
                self.assertTrue((subject.FIXTURES / subject.CALLER_SOURCES[caller["kind"], caller["language"]]).is_file())
                keys.add("+".join(derived))
        self.assertFalse(set(subject.COMBINATIONS) & set(subject.CANDIDATES))
        # Every combination of the five features that a plan can have runs once at least, and each multi-instance combination with
        # the caller's own instance DB besides multiInstanceDependents (the plain fbCaller one); all are verified since 2 November.
        self.assertEqual(18, len(keys)); self.assertEqual(set(), keys - verified)


if __name__ == "__main__":
    unittest.main()
