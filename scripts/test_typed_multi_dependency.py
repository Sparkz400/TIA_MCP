"""Independent fixture/preservation checks; no native process or MCP calls."""
import copy
import json
import pathlib
import tempfile
import types
import unittest
from unittest import mock
from xml.etree import ElementTree as ET

import qualify_typed_multi_dependency as runner
import qualify_typed_resolution as resolution
from block_test_runtime import NativeToolError
from run_conformance import native_document, structural_xml


class MultiDependencyTests(unittest.TestCase):
    def fixture(self, language):
        root = runner.ROOT / "fixtures/typed-graphical/native"
        reference = (root / ("Tgp_FB_multi_" + language + ".xml")).read_text(encoding="utf-8-sig")
        marker = (root / "instance-effects/before-caller.xml").read_text(encoding="utf-8-sig")
        caller = runner.caller_fixture(reference, marker)
        db = native_document((root / "instance-effects/before-instance.xml").read_text(encoding="utf-8-sig"))
        attrs = db.find("SW.Blocks.InstanceDB/AttributeList")
        attrs.find("Name").text = "Tgp_FB_multi_" + language + "_RetainedDB"
        attrs.find("InstanceOfName").text = "Tgp_FB_multi_" + language
        # Build an offline direct-DB stand-in; it is never claimed as native evidence.
        runner.static_section(db).insert(0, copy.deepcopy(runner.member(native_document(caller), "Child")))
        return reference, caller, ET.tostring(db, encoding="unicode")

    def appended(self, xml, name):
        document = native_document(xml)
        child = copy.deepcopy(runner.member(document, "Child"))
        child.set("Name", name)
        runner.static_section(document).append(child)
        return ET.tostring(document, encoding="unicode")

    def test_caller_adds_only_captured_marker_for_both_languages(self):
        for language in ("LAD", "FBD"):
            reference, caller, db = self.fixture(language)
            runner.marker_value(caller, 17)
            runner.marker_value(db, 42)
            reduced = native_document(caller)
            runner.static_section(reduced).remove(runner.member(reduced, "RetainedMarker"))
            self.assertEqual(structural_xml(native_document(reference)), structural_xml(reduced))
            with self.assertRaises(RuntimeError):
                runner.caller_fixture(caller, caller)

    def test_append_oracle_rejects_value_id_comment_existing_child_and_unknown_drift(self):
        for language in ("LAD", "FBD"):
            _, caller, db = self.fixture(language)
            for before, value, network in ((caller, 17, "3"), (db, 42, None)):
                after = self.appended(before, "Fresh")
                runner.verify_append(before, after, "Fresh", value, network)
                edits = [lambda document: runner.member(document, "RetainedMarker").set("Remanence", "Retain"),
                         lambda document: next(node for node in runner.member(document, "RetainedMarker") if runner.local(node) == "StartValue").__setattr__("text", "0"),
                         lambda document: runner.member(document, "Child").set("Datatype", '"WrongFB"'),
                         lambda document: runner.member(document, "Fresh").set("Datatype", '"WrongFB"'),
                         lambda document: document.find(".//MultilingualText").set("ID", "FFFF"),
                         lambda document: ET.SubElement(document, "UnknownExtension", {"Drift": "yes"}),
                         lambda document: next(node for node in document.iter("Text")).__setattr__("text", "Changed")]
                for edit in edits:
                    corrupt = native_document(after)
                    edit(corrupt)
                    with self.assertRaises(RuntimeError):
                        runner.verify_append(before, ET.tostring(corrupt, encoding="unicode"), "Fresh", value, network)
                corrupt = native_document(after)
                static = runner.static_section(corrupt)
                appended = runner.member(corrupt, "Fresh")
                static.remove(appended)
                static.insert(0, appended)
                with self.assertRaises(RuntimeError):
                    runner.verify_append(before, ET.tostring(corrupt, encoding="unicode"), "Fresh", value, network)

    def state(self, root, label, caller, db, language):
        folder = root / label
        folder.mkdir()
        names = ["Tgp_FB_multi_" + language, "Tgp_FB_multi_" + language + "_RetainedDB", "TiaMcp_EnableGate"]
        objects = {}
        for name, text in zip(names, (caller, db, "<Document><Payload>unchanged peer</Payload></Document>")):
            file = folder / (name + ".xml")
            file.write_text(text, encoding="utf-8")
            path = "/plc/blocks/" + name
            objects["block:" + path] = {"path": path, "file": str(file), "payloadSha256": runner.native_hash(text)}
        return {"inventory": {"plcPath": "/plc", "blocks": [{"path": "/plc/blocks/" + name, "name": name, "scopePath": "/plc"} for name in names]}, "objects": objects}

    def test_full_mock_sequence_checks_durable_intent_and_one_restore_then_two_reopens(self):
        for language in ("LAD", "FBD"):
            with tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                args = types.SimpleNamespace(output=root / "evidence", artifacts=root / "native", artifact_policy_root=root,
                    phase="multi-dependency", language=language, timeout=30, host=root / "host", worker=root / "worker", case=["case"])
                run = runner.MultiDependencyRun(args)
                reference, caller, db = self.fixture(language)
                run.reference = reference
                run.caller_name = "Tgp_FB_multi_" + language
                run.db_name = run.caller_name + "_RetainedDB"
                name = run.specification["payload"]["instances"][0]["name"]
                changed_caller = runner.adapt_reference_bindings(self.appended(caller, name), run.specification["referenceBindings"])
                changed_db = self.appended(db, name)
                before = self.state(root, "baseline", caller, db, language)
                after = self.state(root, "changed", changed_caller, changed_db, language)
                prepared = {"changeId": "one", "previewHash": "p", "preview": {"typed": {"nativeArtifact": "caller"},
                    "effects": [{"action": "nativeDependentRecompile", "path": "/plc/blocks/" + run.db_name, "expectedArtifact": "db"}]}}
                calls = []
                class Session:
                    current_project = "original.ap21"
                    def call(self, tool, arguments):
                        calls.append((tool, arguments))
                        if tool == "change_prepare":
                            return prepared
                        if tool == "change_apply":
                            durable = json.loads((args.output / "result.json").read_bytes())["cases"][0]
                            assert durable["applyAttempt"]["idempotencyKey"] == arguments["idempotencyKey"]
                            assert durable["applyAttempt"]["dispatched"]
                            return {"state": "applied", "compile": {"errorCount": 0}, "stages": ["changed_project_saved"]}
                        if tool == "change_restore":
                            self.current_project = "recovered.ap21"
                            return {"state": "restored"}
                        raise AssertionError(tool)
                    def reopen(self, path):
                        calls.append(("reopen", path))
                        self.current_project = path
                run.session = Session()
                with mock.patch.object(runner, "snapshot", side_effect=[before, before, after, after, before]), \
                        mock.patch.object(resolution, "snapshot", return_value=before), \
                        mock.patch.object(runner, "artifact_bytes", side_effect=lambda call, artifact: (changed_caller if artifact == "caller" else changed_db).encode()):
                    run.run_case("case")
                self.assertEqual(["change_prepare", "change_apply", "change_restore", "reopen", "reopen"], [tool for tool, _ in calls])
                self.assertEqual(["original.ap21", "recovered.ap21"], [path for tool, path in calls if tool == "reopen"])
                case = run.report["cases"][0]
                self.assertEqual("passed", case["status"])
                self.assertTrue(all(case[gate] for gate in ("engineeringVerified", "preservationVerified", "persistenceVerified", "recoveryVerified")))
                self.assertFalse(case["runtimeVerified"])
                self.assertEqual(1, len(run.report["restoreAttempts"]))


    def test_uncertain_apply_inspects_exact_status_never_replays_and_restores_only_resolved_checkpoint(self):
        for state, archive, expected_restores in (("applying", True, 0), ("outcome_unknown", True, 0),
                                                ("applied", False, 0), ("applied", True, 1), ("recovery_required", True, 1)):
            with self.subTest(state=state, archive=archive), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                args = types.SimpleNamespace(output=root / "evidence", artifacts=root / "native", artifact_policy_root=root,
                    phase="multi-dependency", language="FBD", timeout=30, host=root / "host", worker=root / "worker", case=["case"])
                run = runner.MultiDependencyRun(args)
                reference, caller, db = self.fixture("FBD")
                run.reference, run.caller_name, run.db_name = reference, "Tgp_FB_multi_FBD", "Tgp_FB_multi_FBD_RetainedDB"
                name = run.specification["payload"]["instances"][0]["name"]
                changed_caller = runner.adapt_reference_bindings(self.appended(caller, name), run.specification["referenceBindings"])
                changed_db = self.appended(db, name)
                before = self.state(root, "baseline", caller, db, "FBD")
                prepared = {"changeId": "exact-attempt", "previewHash": "p", "preview": {"typed": {"nativeArtifact": "caller"},
                    "effects": [{"action": "nativeDependentRecompile", "path": "/plc/blocks/" + run.db_name, "expectedArtifact": "db"}]}}
                calls = []
                class Session:
                    current_project = "original.ap21"
                    def __init__(self, *args, **kwargs):
                        pass
                    def call(self, tool, arguments):
                        calls.append((tool, arguments))
                        if tool == "change_prepare":
                            return prepared
                        if tool == "change_apply":
                            durable = json.loads((args.output / "result.json").read_bytes())["cases"][0]["applyAttempt"]
                            assert durable["changeId"] == "exact-attempt" and durable["dispatched"]
                            raise NativeToolError(tool, {"error": {"code": "TimeoutOutcomeUnknown"}})
                        if tool == "change_status":
                            assert arguments == {"changeId": "exact-attempt"}
                            return dict(state=state, **({"recoveryArchive": {"path": "checkpoint.zap21"}} if archive else {}))
                        if tool == "change_restore":
                            assert arguments == {"changeId": "exact-attempt", "previewHash": "p"}
                            self.current_project = "recovered.ap21"
                            return {"state": "restored"}
                        raise AssertionError(tool)
                    def close(self):
                        calls.append(("close", {}))
                with mock.patch.object(resolution, "OwnedScratch", Session), mock.patch.object(run, "setup"), \
                        mock.patch.object(runner, "snapshot", return_value=before), \
                        mock.patch.object(resolution, "snapshot", return_value=before), \
                        mock.patch.object(runner, "artifact_bytes", side_effect=lambda call, artifact: (changed_caller if artifact == "caller" else changed_db).encode()):
                    self.assertEqual(1, run.run())
                tools = [tool for tool, _ in calls]
                self.assertEqual(1, tools.count("change_apply"))
                self.assertEqual(1, tools.count("change_status"))
                self.assertEqual(expected_restores, tools.count("change_restore"))
                self.assertEqual("close", tools[-1])
                self.assertEqual("blocked", run.report["status"])
                self.assertFalse(run.report["cases"][0]["engineeringVerified"])
                self.assertEqual(bool(expected_restores), run.report.get("failureOriginalXmlVerified", False))


if __name__ == "__main__":
    unittest.main()
