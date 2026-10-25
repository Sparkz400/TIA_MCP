"""Offline admission, lifetime and complete peer-preservation contract tests."""
import copy
import hashlib
import json
import pathlib
import tempfile
import types
import unittest
from unittest import mock
from xml.etree import ElementTree as ET

from qualify_typed_graphical import run_batch, snapshot, assert_snapshot, native_hash
from typed_graphical_owned_session import TypedOwnedSession
from test_typed_resolution_native_harness import FakeClient, FakeWatcher
from typed_graphical_native_expectations import assert_existing_caller_insertion
from qualify_typed_resolution import unqualified_outer_fixture, verify_untouched_caller, verify_tag_address_change
from run_conformance import native_document, structural_xml
from qualify_typed_tag_ambiguity import verify_native_setup
from capture_typed_routes_owned import authored_reference, verify_reference_additions, require_clean_compile
from qualify_typed_multi_dependency import verify_append
from typed_graphical_runtime_preservation import RuntimePreservation, ObservedRuntimeBackend, file_inventory, inventory_difference


class BatchTests(unittest.TestCase):
    def run_fixture(self, mutate=None, initial_stop=False):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            stop = root / "stop"
            if initial_stop:
                stop.touch()
            args = types.SimpleNamespace(output=root / "run", stop_file=stop)
            admitted = []
            def runner(case, selected, destination):
                admitted.append(case["fixtureId"])
                self.assertEqual(args, selected)
                self.assertEqual(destination.name, case["fixtureId"])
                return mutate(stop) if mutate else {"status": "passed"}
            code = run_batch([{"fixtureId": "one"}, {"fixtureId": "two"}], args, runner)
            return code, admitted, json.loads((args.output / "batch.json").read_bytes())

    def test_preexisting_request_starts_no_case(self):
        code, admitted, batch = self.run_fixture(initial_stop=True)
        self.assertEqual(2, code)
        self.assertEqual([], admitted)
        self.assertEqual(["one", "two"], batch["remaining"])
        self.assertEqual("stopped_by_request", batch["status"])

    def test_stop_finishes_active_case_but_admits_no_next(self):
        def request(stop):
            stop.touch()
            return {"status": "passed"}
        _, admitted, batch = self.run_fixture(request)
        self.assertEqual(["one"], admitted)
        self.assertEqual(["two"], batch["remaining"])
        self.assertTrue(batch["stopRequest"]["requested"])
        self.assertEqual("stopped_by_request", batch["status"])

    def test_blocked_or_cleanup_failure_always_halts_admission(self):
        for result in ({"status": "blocked"}, {"status": "passed", "cleanupFailure": "uncertain exit"}):
            with self.subTest(result=result):
                _, admitted, batch = self.run_fixture(lambda _: result)
                self.assertEqual(["one"], admitted)
                self.assertEqual("blocked", batch["status"])
                self.assertEqual("one", batch["blockedFixtureId"])

    def test_uncertain_exception_is_durable_and_never_replayed(self):
        def uncertain(_):
            raise TimeoutError("outcome unknown")
        _, admitted, batch = self.run_fixture(uncertain)
        self.assertEqual(["one"], admitted)
        self.assertEqual(["one", "two"], batch["remaining"])
        self.assertEqual("TimeoutError", batch["failure"]["type"])

    def test_all_cases_required_for_pass(self):
        code, admitted, batch = self.run_fixture()
        self.assertEqual((0, ["one", "two"], "passed", []), (code, admitted, batch["status"], batch["remaining"]))

    def test_output_cannot_overwrite_previous_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            args = types.SimpleNamespace(output=pathlib.Path(directory))
            with self.assertRaises(FileExistsError):
                run_batch([{"fixtureId": "one"}], args, lambda *args: self.fail("No dispatch"))


class OwnedAdapterTests(unittest.TestCase):
    def diagnostic_session(self, root, client, report, persist=lambda: None):
        return TypedOwnedSession(root / "host", root / "worker", root, report, persist,
            client_factory=lambda *args: client, watcher_factory=FakeWatcher,
            manifest_factory=lambda *args: {"frozen": True})

    def test_cleanup_retains_exact_host_identity_and_every_stderr_line(self):
        with tempfile.TemporaryDirectory() as directory:
            root, calls, report = pathlib.Path(directory), [], {}
            client = FakeClient(root, calls)
            client.process = types.SimpleNamespace(pid=123, returncode=0)
            client.errors = ["diagnostic " + str(i) for i in range(200)]
            client.error_reader = types.SimpleNamespace(is_alive=lambda: False)
            session = self.diagnostic_session(root, client, report)
            session.start()
            client.errors.append("last shutdown diagnostic")
            session.close()
            lifetime = report["sessions"][0]
            self.assertEqual(123, lifetime["hostProcessId"])
            self.assertEqual(200, len(lifetime["clientDiagnosticsAtStart"]["stderr"]))
            diagnostics = lifetime["cleanup"]["clientDiagnostics"]
            self.assertEqual(client.errors, diagnostics["stderr"])
            self.assertTrue(diagnostics["stderrEofObserved"])
            self.assertEqual((123, 0), (diagnostics["hostProcessId"], diagnostics["hostExitCode"]))
            client.errors.append("later observation must not mutate retained snapshot")
            self.assertEqual(201, len(diagnostics["stderr"]))

    def test_observing_stderr_eof_cannot_omit_the_final_reader_line(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            client = FakeClient(root, [])
            client.errors = ["first"]
            def finish_reader():
                client.errors.append("last line before EOF")
                return False
            client.error_reader = types.SimpleNamespace(is_alive=finish_reader)
            session = self.diagnostic_session(root, client, {})
            session.client = client
            diagnostics = session.client_diagnostics()
            self.assertTrue(diagnostics["stderrEofObserved"])
            self.assertEqual(["first", "last line before EOF"], diagnostics["stderr"])

    def test_failed_host_receipt_retains_stderr_and_exact_tia_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            root, report = pathlib.Path(directory), {}
            client = FakeClient(root, [])
            client.process = types.SimpleNamespace(pid=456, returncode=1)
            client.errors = ["worker exit remained pending", "full shutdown detail"]
            client.error_reader = types.SimpleNamespace(is_alive=lambda: True)
            client.close = lambda: {"hostExitCode": 1, "forcedHostStop": False,
                "workerExits": [{"processId": 789, "exitCode": None, "processExitVerified": False}]}
            session = self.diagnostic_session(root, client, report)
            session.start()
            with self.assertRaisesRegex(RuntimeError, "host_close"):
                session.close()
            cleanup = report["sessions"][0]["cleanup"]
            self.assertFalse(cleanup["completed"])
            self.assertTrue(cleanup["tiaProcessExit"]["processExitVerified"])
            self.assertFalse(cleanup["clientDiagnostics"]["stderrEofObserved"])
            self.assertEqual(client.errors, cleanup["clientDiagnostics"]["stderr"])
            self.assertEqual("RuntimeError", cleanup["launcherCloseException"]["type"])
            self.assertIn("Host shutdown was not clean", cleanup["launcherCloseException"]["traceback"])
            self.assertTrue(session.poisoned)

    def test_close_exception_preserves_diagnostics_in_finally(self):
        with tempfile.TemporaryDirectory() as directory:
            root, report = pathlib.Path(directory), {}
            client = FakeClient(root, [])
            client.process = types.SimpleNamespace(pid=456, returncode=None)
            client.errors = []
            def fail_close():
                client.errors.append("stderr immediately before close exception")
                raise OSError("injected close failure")
            client.close = fail_close
            session = self.diagnostic_session(root, client, report)
            session.start()
            with self.assertRaisesRegex(RuntimeError, "host_close"):
                session.close()
            cleanup = report["sessions"][0]["cleanup"]
            self.assertEqual("OSError", cleanup["launcherCloseException"]["type"])
            self.assertIn("fail_close", cleanup["launcherCloseException"]["traceback"])
            self.assertEqual(client.errors, cleanup["clientDiagnostics"]["stderr"])
            self.assertTrue(cleanup["tiaProcessExit"]["processExitVerified"])

    def test_client_constructor_failure_is_durable_and_cannot_replay(self):
        with tempfile.TemporaryDirectory() as directory:
            root, report, persisted = pathlib.Path(directory), {}, []
            def fail_constructor(*args):
                raise OSError("host launch failed")
            session = TypedOwnedSession(root / "host", root / "worker", root, report,
                lambda: persisted.append(copy.deepcopy(report)), client_factory=fail_constructor,
                watcher_factory=FakeWatcher, manifest_factory=lambda *args: {})
            with self.assertRaisesRegex(OSError, "host launch failed"):
                session.start()
            session.close()
            failure = persisted[-1]["sessions"][0]["clientConstructionFailure"]
            self.assertEqual("OSError", failure["type"])
            self.assertIn("fail_constructor", failure["traceback"])
            self.assertIsNone(failure["hostProcessId"])
            self.assertFalse(report["sessions"][0]["startAttempted"])
            with self.assertRaisesRegex(RuntimeError, "Previous owned session"):
                session.start()

    def test_diagnostic_failure_cannot_skip_exact_owned_exit_or_pass_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            root, report = pathlib.Path(directory), {}
            session = self.diagnostic_session(root, FakeClient(root, []), report)
            session.start()
            with mock.patch.object(session, "client_diagnostics", side_effect=OSError("diagnostic unavailable")):
                with self.assertRaisesRegex(RuntimeError, "client_diagnostics"):
                    session.close()
            cleanup = report["sessions"][0]["cleanup"]
            self.assertTrue(cleanup["tiaProcessExit"]["processExitVerified"])
            self.assertFalse(cleanup["completed"])
            self.assertEqual("OSError", cleanup["clientDiagnosticsFailure"]["type"])

    def test_each_reopen_has_new_host_handle_and_preserves_selected_project(self):
        with tempfile.TemporaryDirectory() as directory:
            root, calls, report = pathlib.Path(directory), [], {}
            clients = []
            def create(*args):
                client = FakeClient(root, calls)
                clients.append(client)
                return client
            session = TypedOwnedSession(root / "host", root / "worker", root, report, lambda: None,
                client_factory=create, watcher_factory=FakeWatcher, manifest_factory=lambda *args: {"frozen": True})
            session.invoke("tia_start")
            session.invoke("tia_create_scratch")
            owned_path = session.current_project
            session.invoke("tia_detach")
            session.invoke("tia_start")
            session.invoke("project_open", {"path": owned_path})
            session.invoke("tia_detach")
            session.close()
            self.assertEqual(2, len(clients))
            self.assertTrue(all(row["cleanup"]["completed"] for row in report["sessions"]))
            self.assertEqual(2, sum(name == "host_close" for name, _ in calls))

    def test_archive_receipt_is_required_and_registered_for_guarded_reopen(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            path = root / "retrieved" / "owned.ap21"
            class ArchiveClient(FakeClient):
                def request(self, method, params):
                    if method == "tools/call" and params["name"] == "project_retrieve":
                        self.project = str(path)
                        return {"structuredContent": {"retrieved": True, "project": {"path": self.project}}}
                    return super().request(method, params)
            session = TypedOwnedSession(root / "host", root / "worker", root, {}, lambda: None,
                client_factory=lambda *args: ArchiveClient(root, []), watcher_factory=FakeWatcher,
                manifest_factory=lambda *args: {})
            session.invoke("tia_start")
            session.invoke("project_retrieve", {"archivePath": "checked.zap21", "expectedSha256": "a" * 64})
            self.assertIn(str(path.resolve()), session.known_paths)
            with self.assertRaisesRegex(RuntimeError, "empty owned session"):
                session.invoke("project_retrieve", {})
            session.invoke("tia_detach")


class CompleteSnapshotTests(unittest.TestCase):
    def test_all_type_and_tag_peers_keep_exact_identity_and_complete_xml(self):
        inventory = {"completeness": {"scopeComplete": True, "truncated": False}, "blocks": [],
            "types": [{"name": "State", "path": "/one/State"}, {"name": "State", "path": "/two/State"}],
            "tagTables": [{"name": "Tags", "path": "/tags/Tags"}]}
        values = {row["path"]: '<Document><Peer ID="0"><Text>opaque\r\nretained</Text></Peer></Document>'
                  for rows in (inventory["types"], inventory["tagTables"]) for row in rows}
        observed = []
        def call(tool, arguments):
            observed.append((tool, arguments))
            if tool == "software_inspect":
                return inventory
            return {"artifact": {"path": next(iter(arguments.values()))}}
        with tempfile.TemporaryDirectory() as directory, mock.patch("qualify_typed_graphical.artifact_bytes",
                side_effect=lambda _, artifact: values[artifact["path"]].encode()):
            root = pathlib.Path(directory)
            before = snapshot(call, None, root / "before", {})
            self.assertEqual({"@type:/one/State", "@type:/two/State", "@tagTable:/tags/Tags"}, set(before))
            for path in (root / "before").glob("*.xml"):
                self.assertIn(b"opaque\r\nretained", path.read_bytes())
            values["/tags/Tags"] = values["/tags/Tags"].replace('ID="0"', 'ID="1"')
            after = snapshot(call, None, root / "after", {})
            with self.assertRaisesRegex(AssertionError, "@tagTable:/tags/Tags"):
                assert_snapshot(before, after)
            self.assertEqual(4, sum(name == "type_read" for name, _ in observed))


class InsertionContractTests(unittest.TestCase):
    def fixture(self, language):
        root = pathlib.Path(__file__).resolve().parents[1]
        before = (root / "fixtures/typed-graphical/native" / ("Tgp_Boolean_" + language + ".xml")).read_bytes().decode("utf-8-sig")
        after = ET.fromstring(before)
        objects = next(node for node in after if node.tag in ("SW.Blocks.FC", "SW.Blocks.FB")).find("ObjectList")
        unit = next(node for node in objects if node.tag == "SW.Blocks.CompileUnit")
        inserted = copy.deepcopy(unit)
        ids = [node for node in inserted.iter() if node.get("ID") is not None]
        for index, node in enumerate(ids):
            node.set("ID", format(256 + index, "X"))
        objects.insert(list(objects).index(unit) + 1, inserted)
        return before, after, objects, inserted, unit.get("ID")

    def test_contract_retains_every_existing_object_in_both_languages(self):
        for language in ("LAD", "FBD"):
            before, after, _, inserted, anchor = self.fixture(language)
            assert_existing_caller_insertion(before, ET.tostring(after, encoding="unicode"), inserted.get("ID"), anchor)

    def test_title_comment_declaration_opaque_id_and_network_drift_fail(self):
        for language in ("LAD", "FBD"):
            for kind in ("title-id", "declaration", "comment", "opaque", "existing-network"):
                with self.subTest(language=language, kind=kind):
                    before, after, objects, inserted, anchor = self.fixture(language)
                    if kind == "title-id":
                        next(node for node in objects if node.get("CompositionName") == "Title").set("ID", "ABC")
                    elif kind == "declaration":
                        next(node for node in after.iter() if node.tag.endswith("}Member")).set("Datatype", "Int")
                    elif kind == "comment":
                        next(node for node in after.iter() if node.tag == "Text").text = "changed comment"
                    elif kind == "opaque":
                        ET.SubElement(objects, "UnrequestedOpaque").text = "unexpected"
                    else:
                        next(node for node in objects if node.tag == "SW.Blocks.CompileUnit").set("CompositionName", "changed")
                    with self.assertRaises(AssertionError):
                        assert_existing_caller_insertion(before, ET.tostring(after, encoding="unicode"), inserted.get("ID"), anchor)

    def test_numeric_alias_wrong_placement_and_second_addition_fail(self):
        for kind in ("alias", "placement", "extra"):
            before, after, objects, inserted, anchor = self.fixture("LAD")
            if kind == "alias":
                list(inserted.iter())[1].set("ID", "00")
            elif kind == "placement":
                objects.remove(inserted)
                objects.append(inserted)
            else:
                ET.SubElement(objects, "SW.Blocks.CompileUnit", {"ID": "777"})
            with self.assertRaises(AssertionError):
                assert_existing_caller_insertion(before, ET.tostring(after, encoding="unicode"), inserted.get("ID"), anchor)

    def test_actual_xml_comments_pi_and_qname_bindings_cannot_disappear(self):
        before = '<Document xmlns:q="urn:retained"><SW.Blocks.FB ID="0"><AttributeList><Opaque Kind="q:Type">q:Value</Opaque></AttributeList><ObjectList><!--retained comment--><?vendor retained?><SW.Blocks.CompileUnit ID="1"/></ObjectList></SW.Blocks.FB></Document>'
        after = before.replace('<SW.Blocks.CompileUnit ID="1"/>', '<SW.Blocks.CompileUnit ID="1"/><SW.Blocks.CompileUnit ID="2"/>')
        assert_existing_caller_insertion(before, after, "2", "1")
        for changed in (after.replace('<!--retained comment-->', ''), after.replace('<?vendor retained?>', ''),
                after.replace('urn:retained', 'urn:changed'), after.replace('q:Value', 'q:Other')):
            with self.subTest(changed=changed), self.assertRaises(AssertionError):
                assert_existing_caller_insertion(before, changed, "2", "1")
        before = before.replace('<SW.Blocks.CompileUnit ID="1"/>', '<SW.Blocks.CompileUnit ID="1"/><!--boundary-->')
        after = before.replace('<!--boundary-->', '<SW.Blocks.CompileUnit ID="2"/><!--boundary-->')
        assert_existing_caller_insertion(before, after, "2", "1")
        with self.assertRaisesRegex(AssertionError, "crossed"):
            assert_existing_caller_insertion(before, after.replace('<SW.Blocks.CompileUnit ID="2"/><!--boundary-->',
                '<!--boundary--><SW.Blocks.CompileUnit ID="2"/>'), "2", "1")


class StrictNativePayloadTests(unittest.TestCase):
    def test_typed_snapshot_hash_includes_comment_pi_qname_and_preserved_whitespace(self):
        xml = '<Document xmlns:q="urn:retained"><Payload Type="q:Type"><!--keep--><?vendor keep?><Value xml:space="preserve">  </Value></Payload></Document>'
        for changed in (xml.replace('<!--keep-->', ''), xml.replace('<?vendor keep?>', ''),
                xml.replace('urn:retained', 'urn:changed'), xml.replace('>  <', '><')):
            self.assertNotEqual(native_hash(xml), native_hash(changed))

    def test_caller_contract_retains_comments_pi_and_value_namespace_outside_flgnet(self):
        xml = '<Document xmlns:q="urn:retained"><SW.Blocks.FB ID="0"><AttributeList><Opaque Type="q:Type"/></AttributeList><ObjectList><!--keep--><?vendor keep?><SW.Blocks.CompileUnit ID="1"><AttributeList><NetworkSource><FlgNet xmlns="urn:network"><Parts/></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>'
        verify_untouched_caller(xml, xml, "1")
        for changed in (xml.replace('<!--keep-->', ''), xml.replace('<?vendor keep?>', ''), xml.replace('urn:retained', 'urn:changed')):
            with self.assertRaises(RuntimeError): verify_untouched_caller(xml, changed, "1")

    def test_multi_append_contract_preserves_actual_comments_pi_and_inherited_qnames(self):
        before = '<Document xmlns:q="urn:retained"><SW.Blocks.InstanceDB ID="0"><AttributeList><Interface><Sections xmlns="urn:interface"><Section Name="Static"><Member Name="Child" Datatype="q:Type"/><Member Name="RetainedMarker" Datatype="Int"><StartValue>42</StartValue></Member></Section></Sections></Interface></AttributeList><ObjectList><!--keep--><?vendor keep?></ObjectList></SW.Blocks.InstanceDB></Document>'
        after = before.replace('</Section>', '<Member Name="Fresh" Datatype="q:Type"/></Section>')
        verify_append(before, after, "Fresh", 42)
        for changed in (after.replace('<!--keep-->', ''), after.replace('<?vendor keep?>', ''),
                after.replace('urn:retained', 'urn:changed'), after.replace('Name="Fresh"', 'xmlns:q="urn:changed" Name="Fresh"')):
            with self.subTest(changed=changed), self.assertRaises(RuntimeError): verify_append(before, changed, "Fresh", 42)

    def test_address_only_contract_retains_xml_comment_pi_and_qname_binding(self):
        original = '<Document xmlns:q="urn:retained"><SW.Tags.PlcTagTable><AttributeList><Name>Tags</Name></AttributeList><ObjectList><!--keep--><?vendor keep?><SW.Tags.PlcTag><AttributeList><Name>Ready</Name><LogicalAddress>%M100.0</LogicalAddress><Opaque Type="q:Type"/></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>'
        expected = original.replace('%M100.0', '%M101.0')
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            def state(name, xml):
                file = root / (name + '.xml')
                file.write_text(xml, encoding='utf-8')
                return {'objects': {'tagTable:/tags': {'path': '/tags', 'file': str(file), 'payloadSha256': native_hash(xml)}}}
            before = state('before', original)
            verify_tag_address_change(before, state('after', expected), '/tags', 'Ready', '%M100.0', '%M101.0')
            for xml in (expected.replace('<!--keep-->', ''), expected.replace('<?vendor keep?>', ''), expected.replace('urn:retained', 'urn:changed')):
                with self.assertRaises(RuntimeError):
                    verify_tag_address_change(before, state('changed', xml), '/tags', 'Ready', '%M100.0', '%M101.0')


class NamespaceProbeTests(unittest.TestCase):
    XML = '''<Document><SW.Types.PlcStruct ID="0"><AttributeList>
      <Name>Tgp_ResolutionOuter</Name><Namespace>TiaMcp_UnitA.Alternate</Namespace>
      <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="None">
      <Member Name="Child" Datatype="&quot;TiaMcp_UnitA.Alternate.Tgp_ResolutionInner&quot;"><Sections><Section Name="None"><Member Name="Flag" Datatype="Bool"/></Section></Sections></Member>
      </Section></Sections></Interface></AttributeList><ObjectList><Opaque ID="1">retain text</Opaque></ObjectList></SW.Types.PlcStruct></Document>'''

    def test_only_the_observed_child_reference_spelling_changes(self):
        authored = unqualified_outer_fixture(self.XML)
        expected = native_document(self.XML)
        child = next(node for node in expected.iter() if node.get("Name") == "Child")
        child.set("Datatype", '"Tgp_ResolutionInner"')
        self.assertEqual(structural_xml(expected), structural_xml(native_document(authored)))

    def test_wrong_namespace_missing_or_ambiguous_reference_and_dtd_rejected(self):
        changes = [self.XML.replace("TiaMcp_UnitA.Alternate</Namespace>", "TiaMcp_UnitA</Namespace>"),
            self.XML.replace('Name="Child"', 'Name="Other"'),
            self.XML.replace('<Opaque ID="1">retain text</Opaque>', '<Member Name="Child"/>'),
            '<!DOCTYPE Document>' + self.XML]
        for xml in changes:
            with self.subTest(xml=xml), self.assertRaises((RuntimeError, ValueError)):
                unqualified_outer_fixture(xml)


class DuplicateFixtureTests(unittest.TestCase):
    def test_native_rejection_and_acceptance_both_require_exact_addition_only(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for created in (False, True):
                file = root / (str(created) + ".xml")
                tag = '<SW.Tags.PlcTag><AttributeList><Name>Ready</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%M101.0</LogicalAddress></AttributeList></SW.Tags.PlcTag>' if created else ''
                file.write_text('<Document><SW.Tags.PlcTagTable><AttributeList><Name>Tgp_ResolutionDuplicate</Name></AttributeList><ObjectList>' + tag + '</ObjectList></SW.Tags.PlcTagTable></Document>', encoding="utf-8")
                before = {"objects": {"block:/caller": {"payloadSha256": "original"}}}
                after = {"objects": {**copy.deepcopy(before["objects"]), "tagTable:/duplicate": {
                    "kind": "tagTable", "name": "Tgp_ResolutionDuplicate", "path": "/duplicate", "file": str(file)}},
                    "inventory": {"tagTables": [{"path": "/duplicate", "scopePath": "/plc"}]}}
                verify_native_setup(before, after, "/plc", created)
                for fault in ("scope", "original", "missing", "extra", "tag-count"):
                    mutated = copy.deepcopy(after)
                    if fault == "scope": mutated["inventory"]["tagTables"][0]["scopePath"] = "/other"
                    if fault == "original": mutated["objects"]["block:/caller"]["payloadSha256"] = "changed"
                    if fault == "missing": del mutated["objects"]["block:/caller"]
                    if fault == "extra": mutated["objects"]["extra"] = {}
                    with self.subTest(created=created, fault=fault), self.assertRaises(RuntimeError):
                        verify_native_setup(before, mutated, "/plc", not created if fault == "tag-count" else created)


class RouteCapturePacketTests(unittest.TestCase):
    def test_information_target_is_success_only_with_complete_zero_error_diagnostics(self):
        receipt = {"errorCount": 0, "targets": [{"target": "/synthetic/plc", "errorCount": 0, "state": "Information", "messagesTruncated": False,
            "messages": [{"state": "Information", "children": [{"state": "Information"}]}]}]}
        require_clean_compile(receipt)
        for fault in ("error-message", "target-count", "boolean-count", "missing-count", "truncated", "missing-messages"):
            changed = copy.deepcopy(receipt)
            target = changed["targets"][0]
            if fault == "error-message": target["messages"][0]["children"][0]["state"] = "Error"
            if fault == "target-count": target["errorCount"] = 1
            if fault == "boolean-count": target["errorCount"] = False
            if fault == "missing-count": del target["errorCount"]
            if fault == "truncated": target["messagesTruncated"] = True
            if fault == "missing-messages": del target["messages"]
            with self.subTest(fault=fault), self.assertRaises(RuntimeError):
                require_clean_compile(changed)

    def test_capture_compile_checks_recursive_diagnostics_not_only_zero_count(self):
        receipt = {"errorCount": 0, "targets": [{"target": "/synthetic/plc", "errorCount": 0, "state": "Success", "messagesTruncated": False,
            "messages": [{"state": "Information", "children": [{"state": "Success"}]}]}]}
        require_clean_compile(receipt)
        for fault in ("message", "unknown", "target", "truncated", "missing-truncation", "missing-messages", "count", "missing-targets"):
            changed = copy.deepcopy(receipt)
            if fault == "message": changed["targets"][0]["messages"][0]["children"][0]["state"] = "Error"
            if fault == "target": changed["targets"][0]["state"] = "Error"
            if fault == "unknown": changed["targets"][0]["messages"][0]["state"] = "Other"
            if fault == "truncated": changed["targets"][0]["messagesTruncated"] = True
            if fault == "missing-truncation": del changed["targets"][0]["messagesTruncated"]
            if fault == "missing-messages": del changed["targets"][0]["messages"]
            if fault == "count": changed["targets"][0]["errorCount"] = 1
            if fault == "missing-targets": changed["targets"] = []
            with self.subTest(fault=fault), self.assertRaises(RuntimeError):
                require_clean_compile(changed)

    def test_all_38_authored_inputs_retain_exact_route_names_and_source_hashes(self):
        root = pathlib.Path(__file__).resolve().parents[1]
        packet = json.loads((root / "fixtures/typed-graphical/route-capture-packet.v1.json").read_bytes())
        self.assertEqual("authored-not-executed", packet["status"])
        self.assertEqual(38, len(packet["sources"]))
        names = []
        for entry in packet["sources"]:
            observed, _, _ = authored_reference(root / entry["path"], packet["unit"])
            for key in ("names", "namespace", "route", "authoredFormat"):
                self.assertEqual(entry[key], observed[key])
            self.assertEqual(entry["sha256"], observed["fileSha256"])
            names.extend(entry["names"])
        self.assertEqual(45, len(names))
        self.assertEqual(len(names), len(set(names)))
        self.assertFalse(packet["nativeReferenceQualification"])
        self.assertFalse(packet["typedBuilderQualification"])
        self.assertFalse(packet["runtimeQualification"])

    def test_capture_prerequisites_supply_the_objects_the_lost_baseline_held(self):
        root = pathlib.Path(__file__).resolve().parents[1]
        packet = json.loads((root / "fixtures/typed-graphical/route-capture-prerequisites.v1.json").read_bytes())
        main = json.loads((root / "fixtures/typed-graphical/route-capture-packet.v1.json").read_bytes())
        names = []
        for entry in packet["sources"]:
            observed, _, _ = authored_reference(root / entry["path"], packet["unit"])
            for key in ("names", "namespace", "route", "authoredFormat"):
                self.assertEqual(entry[key], observed[key])
            self.assertEqual(entry["sha256"], observed["fileSha256"])
            names.extend(entry["names"])
        self.assertEqual(["TgpUnit_GlobalTimer", "TgpNamespace_TemplateFB_LAD", "TgpNamespace_TemplateFB_FBD"], names)
        self.assertFalse(set(names) & {name for entry in main["sources"] for name in entry["names"]})

    def test_source_parser_rejects_cross_unit_or_unscoped_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            source = pathlib.Path(directory) / "source.scl"
            for text in ('NAMESPACE Other\nFUNCTION "Callee" : Bool\nEND_FUNCTION\nEND_NAMESPACE',
                    'FUNCTION "Callee" : Bool\nEND_FUNCTION',
                    'NAMESPACE TiaMcp_UnitA\nFUNCTION "X" : Bool\nFUNCTION "X" : Bool'):
                source.write_text(text, encoding="utf-8")
                with self.assertRaises(RuntimeError): authored_reference(source, "TiaMcp_UnitA")

    def test_reference_capture_cannot_modify_old_content_or_escape_declared_additions(self):
        before = {"objects": {"type:/old": {"payloadSha256": "old"}}}
        row = {"kind": "block", "name": "Callee", "path": "/new", "payloadSha256": "new"}
        after = {"objects": {**copy.deepcopy(before["objects"]), "block:/new": row}, "inventory": {"blocks": [
            {"path": "/new", "name": "Callee", "scopePath": "/unit", "identity": {"namespace": "Unit.Alternate"}}]}}
        verify_reference_additions(before, after, ["Callee"], "/unit", "Unit.Alternate")
        for fault in ("delete", "old-change", "extra", "wrong-scope", "wrong-namespace", "wrong-name"):
            changed = copy.deepcopy(after)
            if fault == "delete": del changed["objects"]["type:/old"]
            if fault == "old-change": changed["objects"]["type:/old"]["payloadSha256"] = "drift"
            if fault == "extra": changed["objects"]["type:/extra"] = {"kind": "type", "name": "Unrequested"}
            if fault == "wrong-scope": changed["inventory"]["blocks"][0]["scopePath"] = "/other"
            if fault == "wrong-namespace": changed["inventory"]["blocks"][0]["identity"]["namespace"] = "Other"
            if fault == "wrong-name": changed["objects"]["block:/new"]["name"] = "Unrequested"
            with self.subTest(fault=fault), self.assertRaises(RuntimeError):
                verify_reference_additions(before, changed, ["Callee"], "/unit", "Unit.Alternate")


class RuntimePreservationTests(unittest.TestCase):
    def fixture(self, root, readback=lambda: {"unchanged": True}):
        project = root / "project" / "owned.ap21"
        project.parent.mkdir()
        project.write_bytes(b"project")
        (project.parent / "owned.info").write_bytes(b"companion")
        report = {}
        observer = RuntimePreservation(project, root, report, lambda: None, readback)
        observer.capture_active_project()
        card = root / "card"
        card.mkdir()
        (card / "program.bin").write_bytes(b"native")
        files = [{"path": "program.bin", "bytes": 6, "sha256": hashlib.sha256(b"native").hexdigest()}]
        revision = hashlib.sha256(json.dumps(files, separators=(",", ":")).encode()).hexdigest()
        download = {"completed": True, "instanceId": 0, "instanceName": "owned", "memoryCardPath": str(card),
            "programRevision": revision, "files": files}
        return observer, report, download, {"owned": True, "id": 0, "name": "owned"}

    def test_full_inventory_preserves_info_and_detects_added_removed_changed_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "old.info").write_bytes(b"old")
            (root / "gone").write_bytes(b"gone")
            before = file_inventory(root)
            (root / "old.info").write_bytes(b"changed")
            (root / "gone").unlink()
            (root / "new.info").write_bytes(b"new")
            difference = inventory_difference(before, file_inventory(root))
            self.assertEqual({"preserved": False, "added": ["new.info"], "addedInfoFiles": ["new.info"],
                "removed": ["gone"], "changed": ["old.info"]}, difference)

    def test_directory_enumeration_error_cannot_be_a_complete_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            def walk(*args, **kwargs):
                kwargs["onerror"](PermissionError(13, "denied", str(pathlib.Path(directory) / "unreadable")))
                return iter(())
            with mock.patch("typed_graphical_runtime_preservation.os.walk", side_effect=walk):
                observed = file_inventory(pathlib.Path(directory))
            self.assertFalse(observed["complete"])
            self.assertEqual("PermissionError", observed["errors"][0]["type"])

    def test_new_info_card_drift_does_not_suppress_project_or_plc_observations(self):
        with tempfile.TemporaryDirectory() as directory:
            readback = mock.Mock(return_value={"unchanged": True})
            observer, report, download, owned = self.fixture(pathlib.Path(directory), readback)
            observer.capture_download(download, owned)
            (pathlib.Path(download["memoryCardPath"]) / "new.info").write_bytes(b"drift")
            observer.before_cleanup()
            self.assertFalse(observer.after_cleanup(True))
            readback.assert_called_once_with()
            observations = report["runtimePreservation"]["observations"]
            card = next(row for row in observations if row["target"] == "memoryCard")
            self.assertEqual(["new.info"], card["difference"]["addedInfoFiles"])
            self.assertEqual("passed", next(row for row in observations if row["target"] == "completePlcXml")["status"])
            self.assertFalse(report["runtimePreservation"]["historicalZeroAddressOriginResolved"])

    def test_healthy_observations_are_distinct_from_historical_resolution_and_cannot_repeat(self):
        with tempfile.TemporaryDirectory() as directory:
            observer, report, download, owned = self.fixture(pathlib.Path(directory))
            observer.capture_download(download, owned)
            with self.assertRaisesRegex(RuntimeError, "replay"):
                observer.capture_download(download, owned)
            observer.before_cleanup()
            self.assertTrue(observer.after_cleanup(True))
            self.assertFalse(report["runtimePreservation"]["simulatorGuardsChanged"])
            for operation in (observer.before_cleanup, lambda: observer.after_cleanup(True), observer.capture_active_project):
                with self.assertRaisesRegex(RuntimeError, "replayed"):
                    operation()

    def test_inexact_simulator_identity_never_reads_card(self):
        with tempfile.TemporaryDirectory() as directory:
            observer, _, download, owned = self.fixture(pathlib.Path(directory))
            for changed in ({**owned, "owned": False}, {**owned, "id": False}, {**owned, "id": 1}, {**owned, "name": "other"}):
                with self.subTest(owned=changed), mock.patch("typed_graphical_runtime_preservation.verify_memory_card") as verify:
                    with self.assertRaisesRegex(ValueError, "identity"):
                        observer.capture_download(download, changed)
                    verify.assert_not_called()

    def test_plc_readback_failure_is_retained_and_closed_files_still_observed(self):
        with tempfile.TemporaryDirectory() as directory:
            observer, report, download, owned = self.fixture(pathlib.Path(directory), mock.Mock(side_effect=RuntimeError("identity guard rejected")))
            observer.capture_download(download, owned)
            observer.before_cleanup()
            self.assertFalse(observer.after_cleanup(True))
            rows = report["runtimePreservation"]["observations"]
            self.assertEqual("identity guard rejected", next(row for row in rows if row["target"] == "completePlcXml")["error"]["message"])
            self.assertTrue(any(row["phase"] == "afterVerifiedOwnedTiaCleanup" and row["target"] == "projectAndAllCompanions" for row in rows))

    def test_active_lock_transition_is_retained_but_compared_with_matching_lifecycle(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            project = root / "owned.ap21"
            project.write_bytes(b"project")
            report = {}
            observer = RuntimePreservation(project, root, report, lambda: None, lambda: {})
            lock = root / "session.lock"
            lock.write_bytes(b"active session")
            observer.capture_active_project()
            self.assertEqual(["session.lock"], report["runtimePreservation"]["closedToActiveTransition"]["added"])
            observer.before_cleanup()
            active = next(row for row in report["runtimePreservation"]["observations"] if row["target"] == "activeProjectAndAllCompanions")
            self.assertEqual("passed", active["status"])
            lock.unlink()
            observer.after_cleanup(True)
            final = report["runtimePreservation"]["observations"][-1]
            self.assertEqual("passed", final["status"])

    def test_uncertain_tia_exit_cannot_imply_closed_project_or_verified_preservation(self):
        with tempfile.TemporaryDirectory() as directory:
            observer, report, download, owned = self.fixture(pathlib.Path(directory))
            observer.capture_download(download, owned)
            observer.before_cleanup()
            self.assertFalse(observer.after_cleanup(False))
            evidence = report["runtimePreservation"]
            self.assertFalse(evidence["ownedTiaCleanupVerified"])
            self.assertEqual("afterUnverifiedOwnedTiaCleanupAttempt", evidence["observations"][-1]["phase"])

    def test_observation_exception_does_not_skip_owned_cleanup_or_suppress_error(self):
        calls = []
        observer = mock.Mock()
        def observe():
            calls.append("observe")
            raise RuntimeError("observation failed")
        observer.before_cleanup.side_effect = observe
        backend = ObservedRuntimeBackend(mock.Mock(), {"bindings": {}, "signals": {}}, {}, "credential", preservation=observer)
        with mock.patch("block_test_runtime.McpRuntimeBackend.close", side_effect=lambda: calls.append("cleanup") or {"completed": True}):
            with self.assertRaisesRegex(RuntimeError, "observation failed"):
                backend.close()
            with self.assertRaisesRegex(RuntimeError, "replayed"):
                backend.close()
        self.assertEqual(["observe", "cleanup"], calls)

    def test_uncertain_production_cleanup_is_never_promoted_by_preservation(self):
        observer = mock.Mock()
        backend = ObservedRuntimeBackend(mock.Mock(), {"bindings": {}, "signals": {}}, {}, "credential", preservation=observer)
        uncertain = {"completed": False, "runtimeStateUncertain": True, "runtimeOutcomes": [{"kind": "unresolved"}]}
        with mock.patch("block_test_runtime.McpRuntimeBackend.close", return_value=uncertain):
            self.assertEqual(uncertain, backend.close())
        observer.before_cleanup.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
